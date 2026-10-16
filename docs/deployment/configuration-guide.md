# SupportForge AI — Configuration Guide

## Required configuration (`appsettings.Production.json` or environment variables)

| Key | Purpose | Example |
|---|---|---|
| `OpenAI:ApiKey` | LLM completions, embeddings, vision | `sk-...` |
| `VectorStore:Provider` | `Chroma` (local) or `Pinecone` (prod) | `Pinecone` |
| `VectorStore:Chroma:BaseUrl` | Chroma server URL (local only) | `http://localhost:8000` |
| `VectorStore:Pinecone:ApiKey` | Pinecone API key (prod only) | `...` |
| `VectorStore:Pinecone:Environment` | Pinecone environment/region | `us-east-1` |

As environment variables, replace `:` with `__` (double underscore) — e.g. `OpenAI:ApiKey` becomes `OpenAI__ApiKey`. This is .NET's configuration provider convention; setting `OpenAI:ApiKey` as a literal env var name will not bind.

> **Note**: this file has other uncommitted local modifications describing a newer `Llm:*` config scheme superseding the `OpenAI:ApiKey`-only table above; that predates this section and is out of scope here — reconcile the two before treating either as canonical.

## LLM provider configuration (`Llm:Provider` / `Embeddings:Provider`)

`Llm:Provider` selects the chat/vision provider; `Embeddings:Provider` optionally selects a *different* provider for embeddings and defaults to `Llm:Provider` when unset. Every provider populates `LastTotalTokens` consistently, since cost observability depends on it.

| `Llm:Provider` value | Chat | Vision | Embeddings | Config section |
|---|---|---|---|---|
| `OpenAI` | yes | yes | yes | `Llm:OpenAI` (`BaseUrl`, `ChatModel`, `EmbeddingModel`, `ApiKey`) |
| `NvidiaNim` | yes | yes | yes | `Llm:NvidiaNim` (adds `EmbeddingInputType` for asymmetric embedding models) |
| `Azure` | yes | yes | yes | `Llm:Azure` (`BaseUrl` = resource endpoint, `ChatModel`/`EmbeddingModel` = **deployment names**, `ApiKey`) — reuses the OpenAI client under the hood, since Azure.AI.OpenAI returns the same SDK types |
| `Anthropic` | yes | yes | **no** | `Llm:Anthropic` (`BaseUrl`, `ChatModel`, `ApiKey`) — Anthropic ships no embeddings API, so `Embeddings:Provider` **must** be set to a different provider or startup fails with a clear error |
| `Bedrock` | yes | yes | yes | `Llm:Bedrock` (`Region`, `ChatModel`, `EmbeddingModel`) — no `ApiKey`; uses the AWS SDK's standard credential chain (IAM role, env vars, `~/.aws/credentials`) |

Example: Anthropic for chat/vision with NVIDIA NIM for embeddings —
```json
"Llm": { "Provider": "Anthropic", "Anthropic": { "ApiKey": "sk-ant-..." } },
"Embeddings": { "Provider": "NvidiaNim" }
```

### Code knowledge graph (`Neo4j`)

Code Q&A is backed by a Neo4j graph, scoped per project via the `projectId` node property. Configure the connection under `Neo4j` (`Uri`, `User`, `Password`, `Database`). `GraphImportJob` loads each repo's code graph into it; `GraphDbQueryTool` queries it over a pooled `Neo4j.Driver` connection — no external CLI or per-repo subprocess involved.

## Input guard (prompt injection and jailbreak)

The input guard screens each chat turn for prompt injection and jailbreak attempts. It runs in `ChatController` on both `/api/chat/query` and `/api/chat/query/stream`, before intent classification and before any LLM call. It screens the customer's message and recent customer history.

A blocked turn gets a fixed refusal ("I can only help with questions about this product. Could you rephrase what you need help with?"). It costs no LLM tokens, and the assistant message is stored with `GuardVerdict` set to `blocked`. Blocked turns are dropped from later conversation recaps, so one blocked attempt does not carry into later turns or into the guard's own screening of them.

### Configuration

The guard reads the `Guard` section of the configuration. Each key can be overridden with an environment variable, replacing `:` with `__`, for example `Guard__Mode=Shadow`.

| Key | Default | Meaning |
|---|---|---|
| `Guard:Mode` | `Off` | `Off`, `Shadow` or `Enforce`. See Modes below. |
| `Guard:FailClosed` | `false` | When `true` and `Mode` is `Enforce`, a turn is refused if the Jev check fails. Has no effect in other modes. |
| `Guard:CombineWithTriage` | `false` | Reserved. Not read yet. |
| `Guard:DefaultBlockThreshold` | `0.9` | Jev score at or above which a block-class signal fires, for signals with no entry in `Thresholds`. |
| `Guard:DefaultReviewThreshold` | `0.8` | Same, for review-class signals. |
| `Guard:Thresholds` | `{}` | Per-signal thresholds keyed by signal name, for example `"jailbreak_roleplay": 0.95`. These override the defaults above. Used for Jev scores only. |
| `Guard:IndirectThreshold` | `0.8` | Reserved for screening indirect injection. Not read yet. |
| `Guard:HistoryTurns` | `4` | How much recent history is screened. The heuristic reads the last N customer messages. The Jev request is trimmed to the last N history entries. The recap holds at most 6 messages, so larger values have no further effect. |
| `Guard:MaxStateChars` | `8000` | Reserved for the Jev request. Not read yet. |
| `Guard:JevTimeout` | `00:00:01.5` | Time limit for the Jev check. A slower check counts as a failure. |
| `Guard:RepeatBlockWindow` | `00:10:00` | Window in which a user's blocks are counted. |
| `Guard:RepeatBlockCount` | `3` | Number of Jev-decided blocks in the window that triggers the repeat-offender log entry. Logging only. No user is suspended or rate-limited. |

### Modes

- `Off` (default): the guard does nothing. Nothing is scored, logged or stored.
- `Shadow`: each turn is scored and logged. A turn is never blocked. Turns that would be blocked are recorded as flagged.
- `Enforce`: a turn is blocked when Jev scores a block-class signal at or above its threshold. Review-class signals only flag.

### Signals

The guard scores six signals. A signal fires when its score is at or above its threshold.

| Signal | Class | What it covers | Heuristic |
|---|---|---|---|
| `override_instructions` | block | The message tries to make the assistant ignore, override or replace its instructions or role. | Yes |
| `extract_system_prompt` | block | The message asks for hidden instructions, the system prompt or internal configuration. | Yes |
| `jailbreak_roleplay` | block | Role-play, hypotheticals, fictional framing or encoded text used to break the rules. | Yes |
| `cross_tenant_probe` | block | The message asks for data belonging to other customers, organisations or projects. | Yes |
| `extract_raw_code` | review | The message asks for raw source code or file contents to be output verbatim. | No |
| `obfuscated_payload` | review | The message contains encoded or obfuscated text that hides its meaning. | Yes |

The heuristic is a fixed set of phrase patterns, applied after Unicode NFKC normalisation and removal of zero-width and bidirectional control characters. It also decodes base64 runs of 40 or more characters. If a run decodes to printable UTF-8, the decoded text is scanned too and `obfuscated_payload` fires. The phrase list favours precision over recall. It has no pattern for `extract_raw_code`, so that signal fires only from Jev.

### Current limitation: heuristic-only

The Jev client is not wired in yet. The guard therefore runs heuristic-only, and in every mode it can only flag a turn, never block one. Heuristic hits never block on their own. A startup warning is logged whenever `Guard:Mode` is not `Off`.

### Fail-open

If the Jev check errors or times out, the turn proceeds with verdict `unscreened`, and the existing defences still apply. Setting `FailClosed` to `true` in `Enforce` mode refuses the turn instead. A refusal caused by `FailClosed` is not counted toward the repeat-offender entry. Heuristic-only runs have no Jev check to fail, so they never return `unscreened`.

### Logging and telemetry

Log entries carry the user id, the project id, the signal names with their scores, and the source. They never contain message text or history.

- `GuardBlocked` (warning): a turn was blocked.
- `GuardFlagged` (information): a turn was flagged.
- `GuardUnscreened` (information): the Jev check failed and the turn proceeded.
- `GuardRepeatOffender` (warning): a user's blocks in the window reached `RepeatBlockCount`.

A failed Jev check also logs a warning that names the fallback mode.

When `Mode` is not `Off`, each screened turn runs in a `Guard` span with these tags:

- `guard.verdict`: `allowed`, `flagged`, `blocked` or `unscreened`.
- `guard.source`: `heuristic`, `jev`, `jev-failed` or `off`.
- `guard.fired`: comma-separated names of the signals that fired.

Whenever `Mode` is not `Off`, every turn's verdict (`allowed`, `flagged`, `blocked` or `unscreened`) and signal scores are stored on the assistant message as `GuardVerdict` and `GuardSignals`. The customer's message itself is not given those fields.

### Rollout

1. Keep `Guard:Mode` at `Off` until you are ready to observe.
2. Set `Shadow`. Review the flagged turns, which are logged and stored with their verdict and signals.
3. Set `Enforce` only after the guard eval shows 1% or fewer false blocks on hard negatives.

Run the guard eval with `dotnet run --project backend/SupportForge.Evals`. The guard report (`=== Guard eval: ...`) is printed after the main eval report. Its fixtures are in `backend/SupportForge.Evals/Fixtures/guard-fixtures.json`. The guard report does not change the exit code. The whole harness also runs the LLM-based evals, so it needs a configured LLM provider.

Until the Jev guard is run through the eval, the false-block figure is 0% by construction, because heuristic-only runs never block. Use the false-flag figure to judge the heuristic. The 1% false-block bar applies to the Jev-backed run.

### Data note

Once the Jev client is enabled, customer messages and recent history are sent to TypeSafe. Confirm the data-processing terms with TypeSafe before turning it on.

## Onboarding a new project
1. Open the Admin page (`/admin`).
2. Enter a unique Project ID (lowercase, no spaces — used directly as a vector collection prefix) and a display Name.
3. Optionally add one GitHub repo (owner + repo name; the app clones the default branch over HTTPS — for private repos, an access token must be added to `GitHubRepoConfig.AccessTokenSecretName` and the corresponding secret provisioned; this is a known MVP gap, see below).
4. Optionally add a KB Documents folder path (must be reachable from the backend host's filesystem, e.g. a mounted network share).
5. Click "Create Project", then "Re-index" to trigger ingestion.
6. Check `GET /api/projects/{id}/freshness` (or the Dashboard, once wired to it) to confirm ingestion completed.

## Known MVP gaps (explicitly out of scope per TSD §7 risk mitigation)
- Private GitHub repo auth (`AccessTokenSecretName`) is modeled but not wired to a secret store — add before onboarding any private repo.
- Entra ID auth/role-based access (PRD §7) is not implemented in this 7-day plan — the API and UI are unauthenticated. This must land before any external-facing deployment.
- Only one KB connector (local Documents) ships; Confluence is deferred to Phase 2 per PRD §9 roadmap.
- Analytics/Monitoring screen (PRD §5d) is limited to the token-usage counter from Task 20 — full dashboard is Phase 2.

## Known deviations from the original plan
- **.NET 9 instead of .NET 8**: the plan's Tech Stack section specifies .NET 8, but only the .NET 9 SDK was available in the build environment, so all projects target `net9.0`. This was a deliberate, human-approved decision. If .NET 8 is later required (e.g. for a specific hosting constraint), retargeting should be low-risk since no .NET 8-specific APIs are used.
- **Dark/light mode toggle**: not covered by any single task in the original 24-task plan (a gap discovered during Task 19's review); added as a small follow-up fix wiring `useAppStore`'s existing `theme`/`toggleTheme` state to a visible `ThemeToggle` component in `App.tsx`.
- **Token usage bug fix**: Task 20's own reference code had a bug (OpenAI's `total_tokens` JSON field is snake_case, but the C# DTO's case-insensitive-only deserialization didn't bridge the underscore) that would have silently recorded 0 tokens for every request in production; fixed with an explicit `[JsonPropertyName("total_tokens")]` attribute, caught by task review before merge.

## Demo script for handover
1. Create a project via `/admin` pointing at a small real KB folder + one small GitHub repo.
2. Trigger ingestion, wait ~30s–2min depending on repo size.
3. From `/query`, ask a KB-answerable question — confirm citations point at the right file.
4. Ask a code-related question — confirm the Code Analyzer agent fires (check `intent` was `code_issue`) and code file citations appear.
5. Upload a screenshot with an error dialog and ask "why am I seeing this?" — confirm Vision findings feed into the draft.
6. Click Mark Useful / Escalate — confirm `App_Data/feedback.json` records the entry.
