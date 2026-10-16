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

Three further layers sit behind it, all under the same `Guard` section. Each is described below:

- **Combined request** (`Guard:CombineWithTriage`): the guard and the triage intent share one Jev request. Optional.
- **Indirect injection** (`Guard:IndirectThreshold`): KB snippets and vision findings that look like instructions to an AI are dropped before the Drafter sees them. It applies when `Verification:Judge` is `Jev`.
- **Output guard** (`Guard:Mode`): the final draft is checked for disclosure of internal instructions and for following injected content. Replaced with a safe message in `Enforce`.

The guard is a detection layer, not a guarantee. The structural defences described in [SECURITY.md](../../SECURITY.md) stay in place in every mode.

A blocked turn gets a fixed refusal ("I can only help with questions about this product. Could you rephrase what you need help with?"). It costs no LLM tokens, and the assistant message is stored with `GuardVerdict` set to `blocked`. Blocked turns are dropped from later conversation recaps, so one blocked attempt does not carry into later turns or into the guard's own screening of them.

### Configuration

The guard reads the `Guard` section of the configuration. Each key can be overridden with an environment variable, replacing `:` with `__`, for example `Guard__Mode=Shadow`.

| Key | Default | Meaning |
|---|---|---|
| `Guard:Mode` | `Off` | `Off`, `Shadow` or `Enforce`. See Modes below. |
| `Guard:FailClosed` | `false` | When `true` and `Mode` is `Enforce`, a turn is refused if the Jev check fails. Has no effect in other modes. |
| `Guard:CombineWithTriage` | `false` | When `true`, the guard and the triage intent share one Jev request, so an allowed turn makes one Jev call instead of two. It needs `Triage:Classifier` set to `Jev` and `Jev:ApiKey`, or startup fails. See [Combined request](#combined-guard-and-triage-request). |
| `Guard:DefaultBlockThreshold` | `0.9` | Jev score at or above which a block-class signal fires, for signals with no entry in `Thresholds`. |
| `Guard:DefaultReviewThreshold` | `0.8` | Same, for review-class signals. |
| `Guard:Thresholds` | `{}` | Per-signal thresholds keyed by signal name, for example `"jailbreak_roleplay": 0.95`. These override the defaults above. The output guard's signals `output_disclosure` and `output_follows_injected` are keyed the same way. Used for Jev scores only. The shipped `appsettings.json` lists placeholder values for every signal. |
| `Guard:IndirectThreshold` | `0.8` | A KB snippet or vision finding that Jev scores at or above this as containing instructions to an AI is dropped before the Drafter sees it. Read only when `Verification:Judge` is `Jev`. See [Indirect injection](#indirect-injection). |
| `Guard:HistoryTurns` | `4` | How much recent history is screened. The heuristic reads the last N customer messages. The Jev request is trimmed to the last N history entries. The recap holds at most 6 messages, so larger values have no further effect. |
| `Guard:MaxStateChars` | `8000` | Upper bound on the text in the guard's Jev request. Oldest history is dropped first, then the message is cut from its start. The output guard uses the same budget for its own request. |
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

### Jev and heuristic-only operation

When `Jev:ApiKey` is set, the guard also screens each turn with Jev, and `Enforce` can block on Jev's scores. See [Jev (TypeSafe)](#jev-typesafe). Without a key the guard runs heuristic-only, and in every mode it can only flag a turn, never block one. Heuristic hits never block on their own. A startup warning is logged whenever `Guard:Mode` is not `Off` and no key is set.

### Fail-open

If the Jev check errors or times out, the turn proceeds with verdict `unscreened`, and the existing defences still apply. Setting `FailClosed` to `true` in `Enforce` mode refuses the turn instead. A refusal caused by `FailClosed` is not counted toward the repeat-offender entry. Heuristic-only runs have no Jev check to fail, so they never return `unscreened`.

`FailClosed` applies to the input guard only. The output guard always fails open: if its Jev check fails, the draft is sent as it is, after the existing leak check.

### Combined guard and triage request

With `Guard:CombineWithTriage` set to `true`, the guard's Noul questions and the triage Choice and Noul questions go in one Jev request. The turn's guard verdict is still applied first:

- A blocked turn discards the triage answer. Triage never runs for it.
- An allowed or flagged turn uses the triage answer from the same request. `TriageAgent` makes no second Jev call. The triage tokens are still counted under `Triage`.
- If the triage part of the answer is missing or malformed, the guard still uses its own answer, and `TriageAgent` runs its normal Jev call with its LLM fallback.
- If the guard's own answer is missing or malformed, the guard fails open as usual, and triage runs its normal path.

The combined request runs under `Guard:JevTimeout`, not `Triage:JevTimeout`. The triage answer is used only when it arrives inside the guard's budget. The combined request is valid only with `Triage:Classifier` set to `Jev`. Any other combination fails at startup. Turn it on after Shadow data shows the block rate and Jev's p95 latency, since it changes what each turn's single request contains.

### Indirect injection

Retrieved content can carry instructions aimed at the assistant, for example text planted in a KB page, a web page, a repository or a screenshot. When `Verification:Judge` is `Jev`, the findings judge asks one more question per snippet and one for vision findings, in the same request as relevance. An item scored at or above `Guard:IndirectThreshold` is removed before relevance is applied, whatever its relevance score.

- If every KB or code snippet is removed, the verifier fails through the normal retry path.
- A flagged vision finding is a vision failure.
- Each drop logs `IndirectInjectionDropped` (warning) with the verifier name, the project id and the dropped snippet positions. The log has no text. The span carries `verify.injection_dropped`. Use the log to find the document that needs fixing.
- The drop happens whenever Jev answers the judge, whatever `Guard:Mode` is set to, because removing content can only make an answer more conservative. A call that falls back to the LLM judge drops nothing, and neither does `Llm` or `Shadow`.

### Output guard

After the draft passes the existing leak check, the output guard asks Jev two questions about it in one request. One asks whether the answer reveals internal instructions, a system prompt, configuration, credentials or another customer's data. The other asks whether the answer follows instructions that came from retrieved content rather than from the customer.

| Mode (`Guard:Mode`) | Behaviour |
|---|---|
| `Off` | No Jev call, no log, no span. |
| `Shadow` | The draft is always sent. A score at or above its threshold logs `OutputGuardTriggered` (information). |
| `Enforce` | If a score is at or above its threshold, the draft is replaced with the safe fallback message, which is the same text the code-leak check uses. Logs `OutputGuardTriggered` (warning). |

- **Thresholds**: each signal uses `Guard:Thresholds` for `output_disclosure` or `output_follows_injected`, and `Guard:DefaultBlockThreshold` (0.9) otherwise. Set them from a Shadow review, not by guesswork.
- **Fail-open**: a Jev error or timeout (`Guard:JevTimeout`) keeps the draft and logs `OutputGuardUnscreened` (warning). There is no `FailClosed` setting for the output guard.
- **Both paths**: the blocking `/query` path and the streaming path share one check. The streaming path buffers the whole draft before it sends anything, so the check applies before the first word is sent.
- **Tokens**: the output guard's Jev usage is recorded under `OutputGuard` in the turn's token total.
- **Data sent**: the customer's question, the draft, the KB snippet text and the vision findings. Code snippets are sent only when `Verification:Jev:IncludeCode` is `true`, as for the findings judge.
- **Rollout**: keep it in `Shadow` until the `OutputGuardTriggered` log has been reviewed for a few weeks. Only then consider `Enforce`.

### Logging and telemetry

Log entries carry the user id, the project id, the signal names with their scores, and the source. They never contain message text, history, drafts or retrieved text.

- `GuardBlocked` (warning): a turn was blocked.
- `GuardFlagged` (information): a turn was flagged.
- `GuardUnscreened` (information): the Jev check failed and the turn proceeded.
- `GuardRepeatOffender` (warning): a user's blocks in the window reached `RepeatBlockCount`.
- `OutputGuardTriggered` (information in `Shadow`, warning in `Enforce`): a draft score reached its threshold. Names and scores only.
- `OutputGuardUnscreened` (warning): the output guard's Jev check failed, and the draft was sent.
- `IndirectInjectionDropped` (warning): a snippet or vision finding was dropped as instructions to an AI. Carries the verifier name and the dropped positions. See [Indirect injection](#indirect-injection).
- `TriagePrecomputed` (information): the triage answer came from the guard's combined request.

A failed Jev check also logs a warning that names the fallback mode.

When `Mode` is not `Off`, each screened turn runs in a `Guard` span with these tags (the output guard adds `guard.output_fired` to the `OutputGuard` span):

- `guard.verdict`: `allowed`, `flagged`, `blocked` or `unscreened`.
- `guard.source`: `heuristic`, `jev`, `jev-failed` or `off`.
- `guard.fired`: comma-separated names of the signals that fired.

Whenever `Mode` is not `Off`, every turn's verdict (`allowed`, `flagged`, `blocked` or `unscreened`) and signal scores are stored on the assistant message as `GuardVerdict` and `GuardSignals`. The customer's message itself is not given those fields.

### Rollout

1. Keep `Guard:Mode` at `Off` until you are ready to observe.
2. Set `Shadow`. Review the flagged turns, which are logged and stored with their verdict and signals.
3. Set `Enforce` only after the guard eval shows 1% or fewer false blocks on hard negatives. This applies to the input guard and to the output guard.
4. Enable `CombineWithTriage` only after the Shadow data shows the block rate and Jev's latency. It needs `Triage:Classifier` at `Jev`.
5. Threshold values in `appsettings.json` are placeholders. Replace them with the values from the guard eval (`Guard:Thresholds`), and record the report in the plan's implementation status before turning on `Enforce`.

Run the guard eval with `dotnet run --project backend/SupportForge.Evals`. The guard report (`=== Guard eval: ...`) is printed after the main eval report. Its fixtures are in `backend/SupportForge.Evals/Fixtures/guard-fixtures.json`. The guard report does not change the exit code. The whole harness also runs the LLM-based evals, so it needs a configured LLM provider.

With no Jev key, the heuristic run's false-block figure is 0% by construction, because heuristic-only runs never block. Use the false-flag figure to judge the heuristic. When a Jev key is set (`Jev__ApiKey`), the report also has a Jev run (`=== Guard eval: jev ...`). The 1% false-block bar applies to that run.

### Data note

Once `Jev:ApiKey` is set, customer messages and recent history are sent to TypeSafe. With `Guard:Mode` at `Shadow` or `Enforce`, every final draft is sent as well, with the retrieved KB text and vision findings it was written from (see [Output guard](#output-guard)). See [Jev (TypeSafe)](#jev-typesafe) for the full list. Confirm the data-processing terms with TypeSafe before turning it on.

## Jev (TypeSafe)

Jev is TypeSafe's classification model. SupportForge uses it for intent classification in triage (see [Triage classifier](#triage-classifier)) and for the input guard. The wire format is in [docs/integrations/typesafe-jev-api.md](../integrations/typesafe-jev-api.md).

Everything Jev-backed is off unless `Jev:ApiKey` is set. Without the key, triage uses the LLM classifier and the guard runs heuristic-only. Setting `Triage:Classifier` to `Jev` or `Shadow` without a key is a configuration error, and startup fails.

| Key | Default | Meaning |
|---|---|---|
| `Jev:BaseUrl` | `https://api.typesafe.ai` | Jev API base URL. Must be absolute. |
| `Jev:ApiKey` | empty | Bearer key. Set it only with the `Jev__ApiKey` environment variable or a secret store, never in a config file. Blank keeps every Jev-backed feature off. |
| `Jev:Model` | `jev-latest` | Model alias sent with each request. The response reports the model that actually ran. |
| `Jev:Timeout` | `00:00:10` | Whole-request HTTP timeout. A backstop only: triage and the guard apply their own tighter time budgets. |
| `Jev:MaxRetries` | `1` | Retries for 408, 429, 5xx and transport failures. Other errors (401, 403, 404, 422) are not retried. |

Data note: turning Jev on sends customer data to TypeSafe. Triage sends the current message, the last `Triage:HistoryTurns` history turns, whether a screenshot is attached and the product version when one is given. The guard sends the current message, the last `Guard:HistoryTurns` turns and whether a screenshot is attached. With `Guard:CombineWithTriage`, the guard's request also carries the product version and the triage questions. The output guard sends the question, the draft and the retrieved text. Confirm TypeSafe's data-processing terms and retention before you set a key in production. This is a new sub-processor for customer messages.

## Triage classifier

`Triage:Classifier` selects how each turn's intent is classified. The default is `Llm`, which behaves as it did before Jev existed.

- `Llm` (default): the LLM classifies the turn. No Jev key is needed.
- `Jev`: Jev classifies the turn and its answer is acted on. If Jev errors, times out or gives an unusable answer, the LLM classifies that same turn instead. The turn's `IntentSource` is then `llm-fallback`, and a `TriageJevFallback` warning is logged.
- `Shadow`: the LLM classifies and its answer is acted on. Jev runs alongside within the same time budget, and each turn logs one `TriageShadow` line with both intents, Jev's confidence and rule, whether they agree, Jev's latency and whether Jev failed. Use this mode to compare the two before switching to `Jev`.

| Key | Default | Meaning |
|---|---|---|
| `Triage:Classifier` | `Llm` | `Llm`, `Jev` or `Shadow` (see the modes above). `Jev` and `Shadow` need `Jev:ApiKey`. |
| `Triage:MinConfidence` | `0.6` | Below this confidence the turn is `unclear`, and the Drafter asks one clarifying question. Placeholder until the triage eval recommends a value. |
| `Triage:MinMargin` | `0.15` | Below this gap between the top label and the runner-up, the turn is `unclear`. Placeholder until the triage eval recommends a value. |
| `Triage:HighConfidence` | `0.85` | Confidence at or above this is the high band. Confidence between `MinConfidence` and this is the medium band, which adds the Drafter hint below. |
| `Triage:SecondaryIntentRouting` | `false` | When `true`, a runner-up label whose probability is at or above `SecondaryMinProbability` also runs the retrieval branch it would use. Off until the eval supports it. |
| `Triage:SecondaryMinProbability` | `0.35` | Minimum runner-up probability for secondary routing. |
| `Triage:HistoryTurns` | `4` | Recent history turns sent to Jev as context. |
| `Triage:MaxStateChars` | `8000` | Upper bound on the serialized Jev request. Oldest history is dropped first, then the query is cut from its start. |
| `Triage:JevTimeout` | `00:00:01.5` | Budget for each Jev call. On timeout the LLM classifies the turn instead. |

Other behaviour:

- **Drafter hint**: in the medium band, the Drafter is told to restate its reading of the question in one clause before it answers. The LLM classifier reports no band, so `Llm` mode prompts do not change.
- **Persistence**: the assistant message stores `Intent`, `IntentConfidence` and `IntentSource`. They are null on blocked turns and on messages saved before these fields existed.
- **Telemetry**: the Triage span carries the tags `triage.intent`, `triage.confidence`, `triage.source`, `triage.rule` and `triage.runner_up`. Jev token usage is recorded under the `Triage` agent in the turn's token total.
- **Thresholds**: the defaults above are placeholders. The triage eval (`dotnet run --project backend/SupportForge.Evals`) prints a calibration table and a recommended `MinConfidence` for the Jev run. The LLM run reports no confidence, so it has no calibration table. The Jev run needs the key, and the evals workflow reads it from the optional `JEV_API_KEY` repository secret. Set the thresholds from that report, and run `Shadow` for at least a week before `Jev`. See [the plan](../plans/2026-10-16-002-feat-jev-intent-classification-plan.md).

## Findings verification (`Verification`)

The KB, code and vision verifiers, and the Drafter's groundedness check, each ask a findings judge whether a retrieved snippet or a vision finding actually answers the question. `Verification:Judge` selects that judge. The default, `Llm`, is the LLM judge and behaves as it did before Jev existed.

- `Llm` (default): the LLM judge decides. No Jev key is needed.
- `Jev`: Jev scores each snippet and keeps those at or above `MinRelevance`. Vision findings are judged by Jev too, and so is the groundedness check when `GroundednessPerSentence` is on. If Jev errors, times out or gives an unusable answer, the LLM judge decides that same call instead, its `Source` is `llm-fallback`, and a `VerifierJevFallback` warning is logged.
- `Shadow`: the LLM judge decides and its answer is acted on. Jev runs alongside within `JevTimeout`, and each verifier call logs one `VerifierShadow` line with the LLM's pick, how many snippets Jev kept, Jev's top score, whether the two agree, and the latencies. A Jev failure is logged and never changes the answer.

Code-graph snippets are judged by the LLM in every mode unless `Verification:Jev:IncludeCode` is `true`, so with the default `false`, `Jev` decides only the KB and vision branches.

| Key | Default | Meaning |
| --- | --- | --- |
| `Verification:Judge` | `Llm` | `Llm`, `Jev` or `Shadow` (see the modes above). `Jev` and `Shadow` need `Jev:ApiKey`, and startup fails without it. |
| `Verification:MinRelevance` | `0.5` | Jev score at or above which a snippet is kept, and the cut-off for a vision finding to count as relevant. Placeholder until the findings relevance eval recommends a value. |
| `Verification:PartialCoverageBelow` | `0.67` | Jev coverage below this marks the branch as partial, and the Drafter is told the context covers only part of the question. |
| `Verification:MaxSnippetChars` | `2000` | Longer snippets are cut before they are sent to Jev. |
| `Verification:MaxStateChars` | `16000` | Upper bound on the serialized Jev request. Snippets that do not fit are scored 0.0 and dropped from the answer. |
| `Verification:HistoryTurns` | `4` | Recent history turns sent to Jev as context. |
| `Verification:JevTimeout` | `00:00:02` | Budget for each Jev call. On timeout the LLM judge decides that call instead. |
| `Verification:GroundednessPerSentence` | `false` | When `true`, Jev judges the Drafter's draft one sentence at a time. When `false`, the LLM judge checks groundedness in every mode. |
| `Verification:MaxSentences` | `20` | Upper bound on the draft sentences judged in the per-sentence check. |
| `Verification:Jev:IncludeCode` | `false` | Code-graph snippets (customer identifiers and file paths) go to Jev only when `true`. |

**Data note**: with `Judge` set to `Jev` or `Shadow`, the customer question, the last `HistoryTurns` history turns, the KB snippet text and the vision findings text (the model's description of the screenshot) are sent to TypeSafe. Code-graph snippets are sent only when `Verification:Jev:IncludeCode` is `true`. With `GroundednessPerSentence` on, the draft text is sent as well. Confirm the data-processing review covers all of this before you set a key in production.

**Telemetry**:

- Logs: `VerifierJevFallback` (warning, a Jev failure on one verifier call, logged with the exception type name and elapsed time only), `VerifierShadow` (information, one line per verifier call in `Shadow`), and `DrafterUnsupportedSentences` (information, the positions of sentences the groundedness check flagged, never their text).
- Span tags: `verify.source` (the judge that decided: `llm`, `jev` or `llm-fallback`), `verify.kept`, `verify.dropped`, `verify.top_score`, `verify.coverage`, and `drafter.unsupported_sentences`.
- Persistence: the assistant message stores `BranchRelevance`, the top score per branch under `kb`, `code` and `vision`. It is set only when a score-producing judge ran, so it is null on the `Llm` path.

**Rollout**:

1. Ship with `Judge` at `Llm`. This changes no behaviour. Run the findings relevance eval (`dotnet run --project backend/SupportForge.Evals`) for the LLM baseline.
2. With a Jev key, the same eval also runs the Jev judge. Set `MinRelevance` from the threshold table (0.3, 0.5 and 0.7 are reported): choose the highest threshold that keeps per-snippet recall at or above the LLM judge's while improving precision.
3. Run `Shadow` for at least a week. Review how often the two judges agree, how often Jev keeps more than one snippet, and whether Jev drops a snippet the LLM picked. Hand-review those drops against feedback.
4. Switch `Judge` to `Jev`. Leave `Jev:IncludeCode` off until the data-processing review covers source identifiers.
5. Turn on `GroundednessPerSentence` last, and only after a separate review of the sentences it flags. Shadow does not judge groundedness per sentence, so start that review on a sample of Jev-mode traffic.

See [the plan](../plans/2026-10-16-002-feat-jev-intent-classification-plan.md) (U12 to U14) for the design.

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
