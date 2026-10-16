---
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-plan-bootstrap
execution: code
---

# Jev Intent Classification for Triage — Plan

## Goal Capsule
- **Objective**: Identify each customer message's intent with a **calibrated confidence** and use it so we answer more accurately. Concretely: (a) route to a clarifying question when the classifier is genuinely unsure, rather than guessing; (b) when a message straddles two intents, retrieve for both; (c) record intent and confidence so feedback can be traced back to triage. We do this by adding [TypeSafe Jev](https://docs.typesafe.ai/introduction) as an intent classifier behind `TriageAgent`, with the current LLM classifier kept as the default and as the fallback.
- **Product authority**: product owner's decision to integrate Jev, following the spike in [`2026-10-16-001-spike-jev-intent-classification.md`](2026-10-16-001-spike-jev-intent-classification.md).
- **Origin document**: the spike above. Its "Proposed integration design" and "Recommendation" sections are carried forward here as U2–U6 and U1/U9.
- **Blocking external dependency**: the **Jev wire format** (endpoint, request/response JSON, usage reporting, error codes) has not been verified against the official API reference. Only the Introduction page has been read. U4 is the only unit that touches the wire format, and it is isolated in one file (`JevWire.cs`). U1–U3 and U6–U8 can be built and merged without a Jev key.

---

## Product Contract

### Context

Verified against the current worktree (branch `claude/wonderful-bohr-mrhgo2`, off `master` at `d20dad2`):

- **[`TriageAgent.cs`](../../backend/SupportForge.Agents/TriageAgent.cs)** makes one `ILlmChatClient.CompleteAsync` call with a "respond with only the label" prompt. It then cleans the free-text reply with `Trim(Decoration)` and lower-casing, and coerces anything outside `KnownLabels` to `unclear`. There is no confidence signal: `unclear` is a label the LLM must pick for itself. Only `context.Query` is classified. `context.History`, which is already populated by `ChatController.BuildInitialContextAsync` (`ChatController.cs:165`), and `context.ScreenshotBase64` are not used. If the LLM throws, the exception propagates and the request fails.
- **Two call sites, one seam.** `CoordinatorPipeline` (the `/query` path, `CoordinatorPipeline.cs:118`) and `ChatController.QueryStream` (`ChatController.cs:413`) both call `TriageAgent.RunAsync`. A change *inside* `TriageAgent` covers both paths, and neither caller needs to change.
- **DI**: `Program.cs:169-171` builds `TriageAgent` with the **cheap-tier** keyed `ILlmChatClient` (`LlmServiceCollectionExtensions.CheapTierKey`).
- **Six downstream gates use string checks on `context.Intent`:**
  - `KbResearcherAgent.cs:25` and `KbResearcherVerifier.cs:58` run for `kb_question`, `code_issue` and `code_question`.
  - `CrossReferenceAgent.cs:38`, `CodeAnalyzerAgent.cs:25` and `CodeAnalyzerVerifier.cs:59` run for `code_issue` and `code_question`.
  - `DrafterAgent` has per-intent prompt instructions (`DrafterAgent.cs:80-96`) and `HasNoUsableContext`, which exempts `unclear` (`DrafterAgent.cs:30`).
  - `VisionAnalyzerAgent` gates on whether a screenshot is attached, not on intent.
- **Tracing**: `AgentExecutor.HandleAsync` (`CoordinatorPipeline.cs:31`) opens one span per agent with `PipelineTelemetry.ActivitySource`. There are no per-agent tags today.
- **Persistence**: `ChatMessage` ([`ChatMessage.cs`](../../backend/SupportForge.Core/Entities/ChatMessage.cs)) stores answer `Confidence`, `Sources`, `TotalTokensUsed` and full pipeline snippets on the assistant message (written by `ChatController.RecordTurnAsync`, which both `/query` and `/query/stream` call), but **not** the intent. So feedback (useful / not useful) cannot be traced back to triage today.
- **HTTP clients**: provider clients are registered with `AddHttpClient<T>` plus `AddResilienceHandler` (`LlmServiceCollectionExtensions.cs:96-142`; covered by `LlmClientResilienceTests.cs`). A Jev client follows the same pattern.
- **Evals**: `SupportForge.Evals/Program.cs` replays fixtures through the *real* agent classes against a live provider. It covers the three verifiers and `CodeNodeClassifier`. **There is no triage eval**, so current triage accuracy is unknown, and nothing exists yet to tune a confidence threshold against.
- **Jev (official Introduction page)**: you send a *state* (text or JSON) plus typed *questions*, and get typed answers back. **Choice** returns `choice`, `probabilities` and `confidence`. **Noul** ("is this statement true?") returns `noul` (0–1). Any mix of question types goes in **one call**, and each question is evaluated in parallel and in isolation. The docs advise keeping questions *atomic* and composing them in code. ⚠️ Secondhand only (to be verified in U4): a bearer-auth REST endpoint, Python/JS SDKs and no .NET SDK, early-access waitlist, 70–500 ms latency, a ~64k-token request cap, and an ~80 req/s limit.

### Requirements

- **R1**: Intent classification sits behind an `IIntentClassifier` seam. Today's LLM logic moves into `LlmIntentClassifier` with **identical behaviour**: the same prompt, the same normalisation, and the existing `TriageAgentTests` still pass.
- **R2**: A `JevIntentClassifier` classifies with one Jev request. The request contains a **Choice** over the five concrete labels (`kb_question`, `code_issue`, `code_question`, `screenshot_error`, `greeting`) and a small set of atomic **Noul** signal questions. `unclear` is **not** a Jev option; it is produced by the confidence policy (R4).
- **R3**: The Jev *state* is JSON containing the current query, the last N history turns (truncated), `hasScreenshot`, and `productVersion` when present. This is so follow-ups like "still broken" are classified in context.
- **R4**: `TriageAgent` owns a deterministic **decision policy**. It sets `unclear` when `confidence < Triage:MinConfidence` **or** `p(top) − p(second) < Triage:MinMargin`, and applies a few documented Noul override rules (U5). Thresholds come from config. Defaults are chosen from the U1 eval, not guessed.
- **R5**: `AgentContext` exposes `IntentConfidence`, `IntentScores` (label → probability), `IntentSignals` (Noul name → 0–1) and `IntentSource` (`llm` | `jev` | `llm-fallback`). `Intent` keeps exactly its current string contract.
- **R6**: Jev **never takes down a request**. On a timeout (`Jev:TimeoutMs`, default 1500), an HTTP error, a 429, or a malformed or partial response, `TriageAgent` falls back to `LlmIntentClassifier` in the same request, logs a warning, and sets `IntentSource = "llm-fallback"`.
- **R7**: `Triage:Classifier` = `Llm` (default) | `Jev` | `Shadow`. **Shadow** runs both classifiers concurrently, *acts on the LLM result*, and logs the Jev decision next to it for comparison on real traffic. Waiting on Jev is capped by the same timeout, so Shadow adds no latency beyond the LLM path.
- **R8**: **Secondary-intent routing** (opt-in via `Triage:SecondaryIntentRouting`, default `false`). When the runner-up label's probability is ≥ `Triage:SecondaryMinProbability` and it would enable a retrieval branch the primary intent skips, that branch also runs. For example, with a primary of `kb_question` (0.55) and a runner-up of `code_question` (0.38), the code branch also runs. All six gates move onto one shared `IntentRouting` helper so there is a single place to change.
- **R9**: `DrafterAgent` gets intent confidence in its context block. When `IntentSource` is not `llm` and confidence is in the "medium" band (between `MinConfidence` and `Triage:HighConfidence`), the prompt tells it to confirm its reading of the question in one short clause before answering. Below `MinConfidence` the existing `unclear` → clarifying-question path applies unchanged.
- **R10**: Intent, confidence and source are persisted on the assistant `ChatMessage`, and tagged on the Triage span (`triage.intent`, `triage.confidence`, `triage.source`, `triage.runner_up`). Jev usage is recorded through `context.AddTokens("Triage", …)` so token dashboards keep working.
- **R11**: A triage eval exists in `SupportForge.Evals`. It runs each configured classifier over a labelled fixture set and reports per-label accuracy, the `unclear` rate, p50/p95 latency, and a **calibration table** (accuracy per confidence bucket). The default thresholds in R4 come from this report.
- **R12**: Self-hosted deployments are unaffected. Without `Jev:ApiKey`, `Triage:Classifier` cannot be `Jev` or `Shadow`: startup fails with a clear configuration error, matching how `AddLlmProviders` rejects invalid provider combinations.

### Scope Boundaries

**In scope**: the classifier seam, the Jev client and classifier, the decision policy, fallback, Shadow mode, the shared intent-routing helper and secondary-intent routing, the Drafter confidence hint, persistence and telemetry, the triage eval, and config and docs.

**Out of scope** (follow-ups once this lands):
- Using Jev Noul for the three verifiers' pass/fail judgments and for `CodeNodeClassifier`.
- Exposing intent or confidence in the end-user chat UI. The persisted fields make an internal "intent" column on the Feedback dashboard a cheap follow-up.
- Multi-intent *answers* (Drafter answering two questions in one reply).
- Changing the six-label taxonomy.

### Key Technical Decisions

1. **Seam inside `TriageAgent`, not a new pipeline node.** Both call sites (`CoordinatorPipeline`, `ChatController.QueryStream`) already call `TriageAgent`, and the per-node OTel span and DI shape are unchanged. A new node would mean hand-mirroring it in `QueryStream`, which `docs/agentic-pipeline.md` already lists as a duplication risk.
2. **`unclear` comes from policy, not from the model.** We use Jev's calibrated `confidence` and margin rather than offering `unclear` as a sixth Choice option. A model is poorly placed to say "I don't know" as a class. This is the main accuracy gain.
3. **Atomic Noul signals in the same call.** The docs say extra questions barely change latency and are evaluated in isolation. Initial set, each one a "gut-check" statement:
   - `has_support_question`: "The latest message contains an actual support question or request, not only a greeting or thanks."
   - `reports_malfunction`: "The user reports that something is broken, failing, or showing an error."
   - `about_source_code`: "The question is about how the product's source code is implemented."
   - `refers_to_image`: "The latest message refers to an attached image or screenshot."

   These start as *logged signals plus a small set of override rules* (U5). They do not form a second classifier. New rules are added only when the U1 eval shows they help.
4. **Wire format isolated.** All Jev JSON shapes live in `JevWire.cs` (internal records plus one mapping function). Once the official API reference is read, only that file changes.
5. **Fallback is synchronous and per-request.** No circuit breaker in v1: `AddResilienceHandler` already gives retry and timeout at the HTTP layer, and a per-request fallback keeps the logic testable. Add a breaker only if Shadow or production telemetry shows sustained outages.
6. **Shadow before cut-over.** Rollout goes `Llm` → `Shadow` (≥ 1 week of real traffic) → `Jev`. U1 tells us whether Jev is better on fixtures. Shadow tells us whether it agrees on *real* customer phrasing and how often it falls back.
7. **Secondary-intent routing is opt-in and only widens retrieval.** A wrong runner-up costs one extra retrieval branch, and the verifiers still gate what reaches the Drafter. It never narrows what the primary intent would already run.

## High-Level Technical Design

```
ChatController.QueryStream ─┐
CoordinatorPipeline ────────┴─> TriageAgent.RunAsync(context)
                                   │  mode = Triage:Classifier
                                   ├─ Llm    → LlmIntentClassifier ───────────────────────────┐
                                   ├─ Jev    → JevIntentClassifier ──(error/timeout)→ Llm ────┤
                                   └─ Shadow → Llm (acted on) ∥ Jev (logged only) ────────────┤
                                                                                              v
                                       IntentDecision { Scores, Confidence, Signals, Tokens, Source }
                                                                                              │
                                       DecisionPolicy: Noul overrides → MinConfidence/MinMargin → unclear?
                                                                                              │
                                       context.Intent / IntentConfidence / IntentScores / IntentSignals / IntentSource
                                                                                              │
                         IntentRouting.NeedsKb(ctx) / NeedsCode(ctx)  ← six gates (+ secondary intent, opt-in)
                                                                                              │
                                       DrafterAgent (confidence band hint) → ChatMessage (persisted) + span tags
```

```csharp
// SupportForge.Agents/Triage/IIntentClassifier.cs
public sealed record IntentDecision(
    IReadOnlyDictionary<string, double> Scores,   // label → probability (LLM: chosen label → 1.0)
    double Confidence,                            // Jev Choice confidence (LLM: 1.0)
    IReadOnlyDictionary<string, double> Signals,  // Noul name → 0..1 (LLM: empty)
    int TokensUsed,
    string Source);                               // "llm" | "jev"

public interface IIntentClassifier
{
    Task<IntentDecision> ClassifyAsync(AgentContext context, IReadOnlyList<string> labels, CancellationToken ct);
}
```

## Implementation Units

### U1. Triage eval: fixtures and harness runner

**Goal**: Measure triage before changing it, and give R4's thresholds an evidence base.

**Requirements**: R11

**Dependencies**: none. Land it first, run it against the current LLM classifier, and record the baseline in the PR description.

**Files**:
- `backend/SupportForge.Evals/Fixtures/triage-fixtures.json` (new)
- `backend/SupportForge.Evals/Program.cs` (modify: add `RunTriageFixturesAsync` and a `TriageFixture` record)
- `backend/SupportForge.Evals/appsettings.json` (modify: `Triage` and `Jev` sections mirroring the API's)

**Approach**:
1. Write about 100 project-agnostic fixtures (same "domain-diverse, synthetic" policy as `classification-fixtures.json`), with `{ id, domain, query, history[], hasScreenshot, expectIntent, ambiguous }`. Cover at least 12 per concrete label and about 10 genuinely ambiguous cases with `expectIntent: "unclear"`. Weight toward known-hard boundaries:
   - bug vs how-to ("why does export take so long?")
   - kb vs code ("where is the retry limit configured?")
   - a greeting that contains a question ("hi! how do I reset my password")
   - follow-ups that only make sense with history ("still the same error")
   - text that mentions a screenshot with no screenshot attached
2. Run every configured classifier (`Llm` always; `Jev` when `Jev:ApiKey` is set) through the **real** `TriageAgent` (classifier + policy). Record the `CaseResult` per case in the existing `Report` format.
3. Add a triage-specific report block: per-label precision/recall, the `unclear` rate, p50/p95 latency, and a calibration table (confidence buckets 0.0–0.5 / 0.5–0.7 / 0.7–0.85 / 0.85–1.0 → accuracy). Also print the recommended `MinConfidence` and `MinMargin`: the lowest values where accuracy above the threshold is ≥ 95% and no more than about 10% of non-ambiguous cases fall to `unclear`.

**Patterns to follow**: `RunKbFixturesAsync` (real agent class, live provider, `CaseResult`), and `LoadFixtures<T>`.

**Test scenarios**: the harness is an offline tool and not CI-gated (the same model as the existing eval runners). It must build in CI, and must skip the Jev runner cleanly when no key is configured.

**Verification**: the baseline report for the LLM classifier is pasted into the PR description.

---

### U2. `IIntentClassifier` seam and `LlmIntentClassifier` extraction

**Goal**: Introduce the seam with zero behaviour change.

**Requirements**: R1

**Dependencies**: none

**Files**:
- `backend/SupportForge.Agents/Triage/IIntentClassifier.cs` (new: interface and `IntentDecision`)
- `backend/SupportForge.Agents/Triage/LlmIntentClassifier.cs` (new: today's prompt, `Decoration`, normalisation)
- `backend/SupportForge.Agents/TriageAgent.cs` (modify: constructor takes `IIntentClassifier`; `KnownLabels` stays here as the single source of truth for the label set)
- `backend/SupportForge.Api/Program.cs` (modify: register `LlmIntentClassifier` with the cheap-tier client, inject it into `TriageAgent`)
- `backend/SupportForge.Api.Tests/Agents/TriageAgentTests.cs` (modify: construct through `LlmIntentClassifier`; **assertions unchanged**)
- `backend/SupportForge.Api.Tests/Agents/LlmIntentClassifierTests.cs` (new)

**Approach**: Move the prompt and normalisation verbatim. `LlmIntentClassifier` returns `Scores = { normalized: 1.0 }`, `Confidence = 1.0`, empty `Signals`, `TokensUsed = _llm.LastTotalTokens`, and `Source = "llm"`. An unmappable reply returns `Scores = { "unclear": 1.0 }`. `TriageAgent` writes `context.Intent` from the top score, so with the LLM classifier the policy (U5) is a no-op because confidence is 1.0.

**Test scenarios**:
- Every existing `TriageAgentTests` case passes unchanged, including the `[Theory]` normalisation rows.
- `LlmIntentClassifier` maps `"bug_report"` → `unclear` and `"**code_question**"` → `code_question`, and reports `LastTotalTokens`.

**Verification**: `dotnet test` is green, and the diff of `TriageAgentTests` assertions is empty.

---

### U3. `AgentContext` intent fields

**Goal**: Carry the richer decision downstream without changing the `Intent` contract.

**Requirements**: R5

**Dependencies**: U2

**Files**:
- `backend/SupportForge.Agents/AgentContext.cs` (modify)

**Approach**: Add `double? IntentConfidence`, `IReadOnlyDictionary<string, double> IntentScores` (default empty), `IReadOnlyDictionary<string, double> IntentSignals` (default empty), `string IntentSource` (default `""`), and a computed `(string Label, double P)? RunnerUpIntent`. `TriageAgent` sets them all.

**Test scenarios**: `RunnerUpIntent` is null for a single-score decision, and correct for a multi-score one where ties break by label order.

---

### U4. `JevClient`: wire format, options and DI

**Goal**: A typed .NET client for one Jev request (state + N questions → N typed answers).

**Requirements**: R2 (transport), R6 (timeouts), R12

**Dependencies**: **the official Jev API reference must be read first** (Quick Start, Choice, Noul, Confidence and API pages from `https://docs.typesafe.ai/llms.txt`). This is the only blocked unit.

**Files**:
- `backend/SupportForge.Agents/Jev/JevOptions.cs` (new: `BaseUrl`, `ApiKey`, `Model`, `TimeoutMs` = 1500, `HistoryTurns` = 4, `MaxStateChars` = 8000)
- `backend/SupportForge.Agents/Jev/JevWire.cs` (new: internal request/response records; the **only** file that knows Jev's JSON)
- `backend/SupportForge.Agents/Jev/JevClient.cs` (new: `Task<JevResult> EvaluateAsync(object state, IReadOnlyList<JevQuestion> questions, CancellationToken ct)`; `JevQuestion` is `Choice(name, prompt, options)` or `Noul(name, statement)`; `JevResult` holds per-question answers plus usage)
- `backend/SupportForge.Agents/LlmServiceCollectionExtensions.cs` (modify: `AddJev(configuration)` → `AddHttpClient<JevClient>` with bearer auth, `Timeout = TimeoutMs`, and the same `AddResilienceHandler` profile the provider clients use, but with **at most one retry** because triage is on the critical path)
- `backend/SupportForge.Api.Tests/Agents/JevClientTests.cs` (new: fake `HttpMessageHandler`, same convention as `ConfluencePageFetcherTests`)

**Approach**: Serialise the state as JSON. Map the response into `JevResult`, failing fast with a `JevResponseException` on a missing question, an unknown choice option, or probabilities that don't sum to roughly 1. Never log the API key or the raw state. Log the request id, latency and per-question result only.

**Test scenarios**:
- A happy path with one Choice and four Noul questions maps every field.
- 401, 429 and 5xx → typed exception (and 429/5xx are retried once by the resilience handler).
- A timeout → `TaskCanceledException`/`TimeoutRejectedException` surfaces.
- A response missing a question → `JevResponseException`.
- A `choice` that isn't one of the options → `JevResponseException`.
- The Authorization header is set, and the key never appears in log output.

---

### U5. `JevIntentClassifier` and decision policy

**Goal**: Classify with Jev and turn its probabilities into `context.Intent`, including `unclear`.

**Requirements**: R2, R3, R4

**Dependencies**: U2, U3, U4 (the classifier can be developed against a mocked `JevClient` before U4's wire format is final)

**Files**:
- `backend/SupportForge.Agents/Triage/JevIntentClassifier.cs` (new)
- `backend/SupportForge.Agents/Triage/TriageDecisionPolicy.cs` (new: a pure static function, `Decide(IntentDecision, TriageOptions) → (intent, appliedRule)`)
- `backend/SupportForge.Agents/Triage/TriageOptions.cs` (new: `Classifier`, `MinConfidence`, `MinMargin`, `HighConfidence`, `SecondaryIntentRouting`, `SecondaryMinProbability`)
- `backend/SupportForge.Api.Tests/Agents/JevIntentClassifierTests.cs`, `TriageDecisionPolicyTests.cs` (new)

**Approach**:
1. **State**: `{ query, history: last HistoryTurns (role, content) truncated to MaxStateChars total with the newest kept, hasScreenshot, productVersion }`.
2. **Questions**:
   - A Choice `intent` over the five concrete labels. Each option carries the same one-line description as today's LLM prompt, so both classifiers share one taxonomy definition (move the descriptions into a `TriageLabels` constant used by both).
   - Plus the four Noul signals from Key Technical Decision 3.
3. **Policy**, in order, first match wins (each rule is logged as `appliedRule`):
   1. top = `greeting` and `has_support_question ≥ 0.5` → take the best non-greeting label, then continue to step 4.
   2. top = `screenshot_error` and `!hasScreenshot` and `refers_to_image < 0.5` → take the best non-screenshot label, then continue to step 4.
   3. top ∈ {`code_issue`, `code_question`} and the two code labels are within `MinMargin` of each other → pick by `reports_malfunction ≥ 0.5 ? code_issue : code_question`. Both route identically, so this only affects Drafter instructions.
   4. `confidence < MinConfidence` or `p(top) − p(second) < MinMargin` (excluding the step 3 pair) → `unclear`.
   5. Otherwise → top.
4. Initial defaults are `MinConfidence` = 0.6, `MinMargin` = 0.15 and `HighConfidence` = 0.85. These are **placeholders, replaced by U1's recommended values** before `Jev` mode is enabled anywhere.

**Test scenarios** (the policy is a pure function, so each rule gets a table-driven test):
- A high-confidence `kb_question` passes through.
- Low confidence → `unclear`.
- A small margin between `kb_question` and `code_question` → `unclear`.
- A small margin between `code_issue` and `code_question` → resolved by `reports_malfunction`, **not** `unclear`.
- `greeting` with `has_support_question` = 0.9 → the next label.
- `screenshot_error` with no screenshot and `refers_to_image` = 0.1 → the next label.
- `screenshot_error` with a screenshot attached → kept.
- State building: history truncation keeps the newest turns, and `MaxStateChars` is respected.

---

### U6. Classifier modes, fallback and Shadow in `TriageAgent`

**Goal**: Safe selection between classifiers, so Jev can never fail a request.

**Requirements**: R6, R7, R12

**Dependencies**: U2, U5

**Files**:
- `backend/SupportForge.Agents/TriageAgent.cs` (modify)
- `backend/SupportForge.Api/Program.cs` (modify: bind `TriageOptions`, register `JevIntentClassifier` only when `Jev:ApiKey` is set, and fail fast when `Triage:Classifier` is `Jev`/`Shadow` without it)
- `backend/SupportForge.Api.Tests/Agents/TriageAgentTests.cs` (extend)

**Approach**:
- `TriageAgent(IIntentClassifier llm, IIntentClassifier? jev, IOptions<TriageOptions>, ILogger)`.
- `Jev` mode: call Jev, and on any exception except caller cancellation (`ct.IsCancellationRequested`), log a warning, call `llm`, and set `IntentSource = "llm-fallback"`.
- `Shadow` mode: start both, `await` the LLM, then wait for Jev only up to the remaining timeout budget. Log a structured `TriageShadow` event (`llmIntent`, `jevIntent`, `jevConfidence`, `agree`, `jevLatencyMs`, `jevFailed`) and act on the LLM decision.
- Count tokens once per classifier that actually ran.

**Test scenarios**:
- `Llm` mode never touches the Jev classifier.
- `Jev` mode: Jev throws → LLM result is used, `IntentSource = "llm-fallback"`, and a warning is logged.
- `Jev` mode: caller cancellation propagates and does not fall back.
- `Shadow` mode acts on the LLM result even when Jev disagrees, and logs `agree = false`.
- `Shadow` mode: a slow Jev doesn't delay past the timeout.
- Startup with `Classifier = Jev` and no key → `InvalidOperationException` with a clear message.

---

### U7. Shared `IntentRouting` and secondary-intent routing

**Goal**: Route all six gates through one place, and optionally retrieve for a strong runner-up intent.

**Requirements**: R8

**Dependencies**: U3

**Files**:
- `backend/SupportForge.Agents/IntentRouting.cs` (new: `NeedsKb(ctx)`, `NeedsCode(ctx)`, both honouring `ctx.SecondaryIntentEnabled` and the runner-up)
- `KbResearcherAgent.cs`, `KbResearcherVerifier.cs`, `CrossReferenceAgent.cs`, `CodeAnalyzerAgent.cs`, `CodeAnalyzerVerifier.cs` (modify: replace the inline `is not (...)` checks)
- `backend/SupportForge.Agents/AgentContext.cs` (modify: `SecondaryIntentEnabled` and `SecondaryMinProbability`, set by `TriageAgent` from options)
- `backend/SupportForge.Api.Tests/Agents/IntentRoutingTests.cs` (new)

**Approach**: First move the existing checks into the helper **verbatim**, as a pure refactor: all existing agent tests stay green. Then add the secondary rule: if it is enabled, the runner-up's probability is ≥ `SecondaryMinProbability`, and the primary isn't `unclear`/`greeting`, return `true` when *either* the primary or the runner-up label needs that branch. `context.Intent` is unchanged, so the Drafter still answers the primary intent.

**Test scenarios**:
- Secondary routing disabled → results match the old gates for all six labels.
- Enabled → primary `kb_question`, runner-up `code_question` at 0.4 → `NeedsCode` is true.
- Runner-up below threshold → false.
- Primary `unclear` → false for both.
- Primary `greeting` → false.

---

### U8. Drafter confidence hint, persistence and telemetry

**Goal**: Use confidence in the answer, and make triage quality observable and traceable from feedback.

**Requirements**: R9, R10

**Dependencies**: U3, U6

**Files**:
- `backend/SupportForge.Agents/DrafterAgent.cs` (modify: add `Intent confidence: high|medium` to the context block, plus one prompt line for the medium band: "briefly state your reading of the question in one clause, then answer")
- `backend/SupportForge.Agents/TriageAgent.cs` (modify: `Activity.Current?.SetTag(...)` for `triage.intent`, `triage.confidence`, `triage.source`, `triage.runner_up` and `triage.rule`)
- `backend/SupportForge.Core/Entities/ChatMessage.cs` (modify: `string? Intent`, `double? IntentConfidence`, `string? IntentSource`; nullable so existing JSON files deserialise)
- `backend/SupportForge.Api/Controllers/ChatController.cs` (modify: set them on the assistant message in `RecordTurnAsync`, the single write site both paths share)
- Tests: `DrafterAgentTests`, `TracingTests`, `ChatControllerTests`, and `JsonFileChatMessageRepository` round-trip (extend)

**Approach**: The hint is **only** emitted when `IntentSource != "llm"`. The LLM classifier's confidence is always 1.0, so `Llm` mode prompts stay byte-identical. That keeps existing Drafter behaviour and evals stable until Jev is on.

**Test scenarios**:
- `Llm`-mode Drafter prompt is unchanged (snapshot).
- A Jev decision in the medium band adds the hint line; the high band doesn't.
- Span tags are present on the Triage span.
- An assistant `ChatMessage` round-trips the new fields.
- An old message JSON without them still loads.

---

### U9. Config, docs and rollout

**Goal**: Make the feature operable and the rollout explicit.

**Requirements**: R7, R12

**Dependencies**: U1–U8

**Files**:
- `backend/SupportForge.Api/appsettings.json` (modify: `Triage` section with `Classifier: "Llm"` and thresholds; `Jev` section with an empty `ApiKey` and a `_ApiKey` comment pointing to the `Jev__ApiKey` env var / secret)
- `docs/deployment/configuration-guide.md` (modify: a Triage/Jev section covering modes, thresholds, data-processing note, and the self-hosted note)
- `docs/agentic-pipeline.md` (modify: describe the Triage classifier modes. The doc is already flagged STALE, so update only the Triage section and leave the staleness banner)
- `README.md` (modify: one line under Features, plus the Configuration table)

**Rollout**:
1. Merge U1 (eval) and U2 + U3 + U7 (behaviour-identical refactors) with `Classifier = Llm`.
2. With a Jev key: merge U4–U6 and U8, run U1 for both classifiers, and set threshold defaults from the report.
3. Run production in **Shadow** for ≥ 1 week. Review agreement rate, Jev fallback rate, Jev p95 latency, and disagreements against feedback.
4. Switch to `Jev` if the Shadow fallback rate is < 1%, the U1 accuracy is ≥ the LLM baseline, and disagreements reviewed by hand favour Jev. Then enable `SecondaryIntentRouting` and watch token cost per turn.
5. Before step 3: confirm data-processing terms with TypeSafe, because customer messages are sent to a new sub-processor.

## Verification Contract

- `dotnet build` and `dotnet test` on `backend/SupportForge.Backend.sln` are green in CI. All pre-existing `TriageAgentTests`, agent gate tests, and `DrafterAgentTests` pass **unchanged** in `Llm` mode.
- New unit tests cover every pure unit: `TriageDecisionPolicy`, `IntentRouting`, `JevClient` (fake handler), classifier selection/fallback/Shadow, persistence round-trip, and span tags.
- The U1 eval is run manually with a live LLM key (baseline) and a Jev key (comparison), and both reports are pasted into the PR. This follows the harness's existing manual verification model; it is not CI-gated.
- Manual smoke test in `Shadow` mode: one `/query` and one `/query/stream` request each log a `TriageShadow` event and return the same answer as `Llm` mode.

## Definition of Done

- U1–U9 implemented, with R1–R12 each covered by at least one unit and test.
- Threshold defaults in `appsettings.json` come from a recorded U1 report, not the placeholders in U5.
- `Triage:Classifier` defaults to `Llm`, and self-hosted deployments need no new config.
- The Shadow-mode review (rollout step 3) is written up before any environment switches to `Jev`.

## Open Questions

1. **Jev API reference** (blocks U4): exact endpoint and request/response JSON, whether usage/token counts are returned, error codes, rate limits, and model/version pinning.
2. **Data processing**: TypeSafe's retention and training-use policy and regions, and whether a DPA is needed for multi-org customers.
3. **Access**: early-access key and the production quota (80 req/s is reported; confirm it covers peak traffic in Shadow mode, which sends every request to Jev).
4. **How Jev's `confidence` relates to Choice `probabilities`** (from the official Confidence page). If confidence is already margin-aware, `MinMargin` may be redundant and can be removed after U1.
