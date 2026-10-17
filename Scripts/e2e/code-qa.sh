#!/usr/bin/env bash
# Code-question accuracy on an external repository (live LLM, Jev, Neo4j).
#
# Ingests the repository named in a code-QA fixture (Scripts/e2e/code-qa/*.json) into the code graph, then asks each
# fixture question through /api/chat/query under two configurations: today's defaults (Llm) and Jev with
# Guard:Mode=Enforce. For each answer it records whether the draft names the expected identifier, whether the
# expected file was among the code details retrieved from the graph, and whether the Drafter replaced the answer
# with its verbatim-code LeakFallback. Writes $OUT/code-qa.md. Report-only: exits 1 only when setup fails.
#
# Needs LLM_API_KEY and JEV_API_KEY. No secret is printed. Usage: code-qa.sh [fixture.json]
set -uo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
API_BIN=${API_BIN:-$ROOT/backend/SupportForge.Api/bin/Release/net9.0}
OUT=${OUT:-$ROOT/e2e-out}
FIXTURE=${1:-$ROOT/Scripts/e2e/code-qa/django.json}
BASE=http://127.0.0.1:5080
NEO4J=${NEO4J_HTTP_URL:-http://localhost:7474}
PROJECT=code-qa
USER_NAME=code-qa-admin
PASSWORD='E2e-Password-123!' # throwaway account on a throwaway runner
LEAK_FALLBACK="I can't share code or document excerpts directly."

mkdir -p "$OUT"
REPORT=$OUT/code-qa.md
: > "$REPORT"
for v in LLM_API_KEY JEV_API_KEY; do
  if [ -z "${!v:-}" ]; then echo "::error::$v is not set"; exit 1; fi
done

export Jwt__Key; Jwt__Key=$(openssl rand -base64 48)
export ASPNETCORE_URLS=$BASE
export Llm__Provider=NvidiaNim
export Llm__ApiKey=$LLM_API_KEY
export Jev__ApiKey=$JEV_API_KEY
export Logging__LogLevel__Default=Information

say() { echo "$*" | tee -a "$REPORT"; }

API_PID=""
start_api() {
  local name=$1; shift
  ( cd "$API_BIN" && exec env "$@" dotnet SupportForge.Api.dll ) >"$OUT/code-qa.$name.log" 2>&1 &
  API_PID=$!
  for _ in $(seq 90); do
    [ "$(curl -s -m 5 -o /dev/null -w '%{http_code}' "$BASE/health" || true)" != "000" ] && return 0
    kill -0 "$API_PID" 2>/dev/null || break
    sleep 1
  done
  echo "::error::API did not start ($name)"; tail -n 40 "$OUT/code-qa.$name.log"; return 1
}
stop_api() { [ -n "$API_PID" ] && kill "$API_PID" 2>/dev/null && wait "$API_PID" 2>/dev/null; API_PID=""; }
trap stop_api EXIT

TOKEN=""
post() { # post <path> <json> <outfile> -> prints HTTP code
  curl -s -m 300 -X POST -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    --data "$2" -o "$3" -w '%{http_code}' "$BASE$1" || true
}

REPO=$(jq -r '.repo' "$FIXTURE")
BRANCH=$(jq -r '.branch' "$FIXTURE")
COUNT=$(jq '.questions | length' "$FIXTURE")

say "# Code questions on $REPO@$BRANCH"
say ""
start_api setup || exit 1

body=$(jq -nc --arg u "$USER_NAME" --arg p "$PASSWORD" \
  '{OrgName:"Code QA Org", UserName:$u, Password:$p, ContactPerson:"QA", ContactNumber:"000", Industry:"Testing"}')
post /api/auth/register "$body" "$OUT/code-qa.register.json" >/dev/null
TOKEN=$(jq -r '.accessToken // empty' "$OUT/code-qa.register.json" 2>/dev/null)
rm -f "$OUT/code-qa.register.json" # holds the token; $OUT is uploaded as an artifact
[ -n "$TOKEN" ] || { echo "::error::could not register"; exit 1; }

body=$(jq -nc --arg id "$PROJECT" --arg o "${REPO%%/*}" --arg r "${REPO#*/}" --arg b "$BRANCH" \
  '{Id:$id, Name:"Code QA", KbSources:[], Repos:[{Owner:$o, Repo:$r, DefaultBranch:$b}]}')
[ "$(post /api/projects "$body" "$OUT/code-qa.project.json")" = "200" ] || { echo "::error::project create failed"; exit 1; }
[ "$(post /api/ingestion/trigger "$(jq -nc --arg id "$PROJECT" '{ProjectId:$id}')" "$OUT/code-qa.ingest.json")" = "202" ] \
  || { echo "::error::ingestion trigger failed"; exit 1; }

# Clone plus graph import of a large repository takes minutes. The project's node count in Neo4j says when it is done.
started=$(date +%s); nodes=0; last=-1; stable=0
cypher=$(jq -nc --arg p "$PROJECT" '{statements:[{statement:"MATCH (n) WHERE n.projectId = $p RETURN count(n)", parameters:{p:$p}}]}')
for i in $(seq 180); do
  nodes=$(curl -s -H 'Content-Type: application/json' --data "$cypher" "$NEO4J/db/neo4j/tx/commit" | jq -r '.results[0].data[0].row[0] // 0')
  [ $((i % 6)) -eq 0 ] && echo "  ingestion: $(( $(date +%s) - started ))s, $nodes nodes"
  if [ "${nodes:-0}" -gt 0 ] 2>/dev/null && [ "$nodes" = "$last" ]; then stable=$((stable + 1)); else stable=0; fi
  [ "$stable" -ge 3 ] && break
  last=$nodes
  sleep 10
done
if [ "${nodes:-0}" -gt 0 ] 2>/dev/null; then
  stop_api
  say "- Ingested: $nodes graph nodes in about $(( $(date +%s) - started ))s"
else
  say "- **No graph nodes reached Neo4j**"
  # Exception types and messages only (no request text exists yet at this point).
  curl -s -H "Authorization: Bearer $TOKEN" "$BASE/api/ingestion/dead-letters" | head -c 2000; echo
  grep -iE 'fail|error|exception|retry' "$OUT/code-qa.setup.log" | tail -n 40
  exit 1
fi

# ask_all <mode>: every fixture question under the running API. Appends one table row per question.
ask_all() {
  local mode=$1 i q answer path file code draft details named retrieved deflected
  local n_named=0 n_retrieved=0 n_deflected=0 n_ok=0
  say ""; say "## $mode"; say ""
  say "| # | Question | Names the answer | Right file retrieved | Deflected | Intent | Conf. |"
  say "|---|---|---|---|---|---|---|"
  for i in $(seq 0 $((COUNT - 1))); do
    q=$(jq -r ".questions[$i].q" "$FIXTURE"); answer=$(jq -r ".questions[$i].answer" "$FIXTURE"); path=$(jq -r ".questions[$i].path" "$FIXTURE")
    file="$OUT/code-qa.$mode.$i.json"
    code=$(post /api/chat/query "$(jq -nc --arg p "$PROJECT" --arg q "$q" '{ProjectId:$p, Query:$q}')" "$file")
    [ "$code" = "200" ] && n_ok=$((n_ok + 1))
    draft=$(jq -r '.draft // ""' "$file" 2>/dev/null)
    details=$(jq -r '[(.codeDetails // [])[], (.sources // [])[].url] | join("\n")' "$file" 2>/dev/null)
    grep -qiF "$answer" <<<"$draft" && { named=yes; n_named=$((n_named + 1)); } || named=no
    grep -qF "$path" <<<"$details" && { retrieved=yes; n_retrieved=$((n_retrieved + 1)); } || retrieved=no
    grep -qF "$LEAK_FALLBACK" <<<"$draft" && { deflected=yes; n_deflected=$((n_deflected + 1)); } || deflected=no
    local intent conf
    intent=$(jq -r --arg c "$(jq -r '.conversationId // ""' "$file" 2>/dev/null)" \
      '[.[] | select(.Role == "assistant" and .ConversationId == $c)] | last | "\(.Intent)"' "$API_BIN/App_Data/messages.json" 2>/dev/null)
    conf=$(jq -r '.confidence // "-"' "$file" 2>/dev/null)
    say "| $((i + 1)) | $q | $named | $retrieved | $deflected | ${intent:-?} | $conf |"
  done
  say ""
  say "**$mode:** HTTP 200 $n_ok/$COUNT, names the answer $n_named/$COUNT, right file retrieved $n_retrieved/$COUNT, deflected by LeakFallback $n_deflected/$COUNT"
  # Labels only: which leak check fired, per attempt (DrafterAgent logs no draft text).
  say "- Leak checks fired ($mode): $(grep -oE 'leak check fired: [a-z-]+ internal=[A-Za-z]+ attempt=[12]' "$OUT/code-qa.$mode.log" \
    | sed 's/leak check fired: //' | sort | uniq -c | awk '{c=$1; $1=""; printf "%s x%s;", $0, c}')"
  say ""
  say "<details><summary>Drafts ($mode)</summary>"; say ""
  for i in $(seq 0 $((COUNT - 1))); do
    say "$((i + 1)). $(jq -r '.draft // "(no draft)"' "$OUT/code-qa.$mode.$i.json" 2>/dev/null | tr '\n' ' ' | cut -c1-300)"
  done
  say ""; say "</details>"
}

if start_api llm; then ask_all llm; stop_api; fi
if start_api jev-enforce Triage__Classifier=Jev Verification__Judge=Jev Guard__Mode=Enforce; then
  ask_all jev-enforce; stop_api
  say "- jev-enforce log: TriageJevFallback x$(grep -c TriageJevFallback "$OUT/code-qa.jev-enforce.log"), GuardBlocked x$(grep -c GuardBlocked "$OUT/code-qa.jev-enforce.log"), OutputGuardTriggered x$(grep -c OutputGuardTriggered "$OUT/code-qa.jev-enforce.log")"
fi
exit 0
