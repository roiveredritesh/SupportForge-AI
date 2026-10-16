# Spike: TypeSafe Jev for Triage intent classification

**Status:** Spike / research only — no code changes. **Decision:** promising fit, run a gated bake-off before adopting (see [Recommendation](#recommendation)).

## Question

Can we replace (or back up) `TriageAgent`'s LLM-prompted intent classification with [TypeSafe AI's Jev](https://docs.typesafe.ai/introduction), and would it actually help this project?

## Caveat on sources

`docs.typesafe.ai` and `api.typesafe.ai` were **blocked by the network policy of the environment this spike ran in**. The official [Introduction](https://docs.typesafe.ai/introduction) page was supplied separately and is marked ✅ below. Everything else comes from third-party write-ups (KDnuggets, MarkTechPost, DataCamp, OpenRouter's listing, dev.to) and is **vendor-reported and unverified**. Items marked ⚠️ must be confirmed against the official docs (index: `https://docs.typesafe.ai/llms.txt`) before any implementation.

## What Jev is

- ✅ A "System One" decision model, **not a text generator**. You send a **state** (text or JSON) plus one or more typed **questions**, and get typed answers back with probability distributions. There is no output to parse.
- ✅ Question types ("primitives"):

  | Type | Goal | Returns |
  |---|---|---|
  | **Choice** | Choose an option from a list | `choice`, `probabilities`, `confidence` |
  | **Score** | Score the state on a rubric | `score`, `probabilities`, `confidence` |
  | **Noul** | Is this statement true? | `noul` (0–1) |

- ✅ `confidence` is reported **separately** from `probabilities` (see the official [Confidence](https://docs.typesafe.ai/confidence) page). Our threshold policy should key on `confidence`, not only on the top probability. ⚠️ How the two relate needs to be read on that page.
- ✅ **Several questions, of any mix of types, go in one call.** Each is evaluated in parallel and in isolation against the same state, and the docs say extra questions barely change response time.
- ✅ Design guidance: keep each question **atomic** ("a gut-check a knowledgeable person could make in a few seconds"). Break anything that needs reasoning into several questions and combine them in code.
- Vendor-reported latency is **~70–500 ms** end-to-end.
- ⚠️ Reported pricing is ~$0.042 per 1M input tokens, with output free. No public enterprise price list or SLA was found.
- ⚠️ Reported API: bearer-token `POST https://api.typesafe.ai/v1/systemone`. SDKs exist for **Python and JavaScript only** (no .NET). Reported limits are 64k tokens/request and 80 req/s.
- ⚠️ Access is **early access / waitlist** (launched mid-Sept 2026). Reported launch dates vary between sources. Nothing published on model architecture, training data, or self-hosting.

## How Triage works today

`backend/SupportForge.Agents/TriageAgent.cs`:

- One `ILlmChatClient.CompleteAsync` call on the **cheap-tier** client (`Program.cs`, `CheapTierKey`) with a prompt that says "respond with only the label".
- Six labels: `kb_question`, `code_issue`, `code_question`, `screenshot_error`, `greeting`, `unclear`.
- The reply is free text. It is cleaned up (`Trim(Decoration)`, lower-cased) and anything that doesn't match a label is coerced to `unclear`.
- `unclear` is something the LLM has to choose for itself. There is no confidence signal.
- Only `context.Query` is classified. Conversation `History` and whether a screenshot is attached are not used.
- It runs **first and serially** (`Triage → FreshnessGate → fan-out`), so its latency is added to every request.
- If it throws, the request fails. There is no fallback classifier.
- Six downstream consumers branch on `context.Intent`: KbResearcher, its verifier, CrossReference, CodeAnalyzer, its verifier, and Drafter.
- **There is no triage eval.** `SupportForge.Evals` covers the three verifiers and `CodeNodeClassifier`, but not triage. Today's triage accuracy is unknown.

## Fit analysis

### Where Jev would help

| Area | Today | With Jev |
|---|---|---|
| Output shape | Free text, then cleanup and fallback to `unclear` | A typed Choice over a fixed label set, so there is no parsing |
| `unclear` handling | The LLM guesses that it is unsure | A **calibrated threshold**: route to `unclear` when Choice `confidence` is below τ or the gap to the runner-up probability is small. This is the biggest qualitative win. |
| Extra signals | One prompt has to do everything | Atomic **Noul** questions in the **same call** at no real latency cost, e.g. "the message contains an actual support question" (separates `greeting` from a greeting that includes a question), "the user reports something is broken or erroring" (bug vs question), "the message refers to an attached image". Combine them with the Choice result in code. |
| Ambiguous cases (e.g. a bug report with a screenshot) | Single label, no signal | The full distribution is available. We could log runner-up intents, and later fan out to both branches. |
| Latency on the critical path | One LLM round-trip, roughly 0.5–2 s depending on provider | Vendor claims 70–500 ms |
| Observability | Intent only | Intent + confidence, which can go on the OTel span for Triage and into feedback analysis |

### Where it hurts, or the risk is real

1. **Vendor maturity.** Early access, a waitlist, no SLA, and unverified benchmarks. Triage is a hard dependency of every request.
2. **No .NET SDK.** We would write a small `HttpClient` adapter against a schema we have not yet verified. This is cheap, but we own it.
3. **Self-hosted deployments.** We support Ollama for fully self-hosted installs, and Jev appears to be hosted-only. It **must stay optional**, with the current LLM path as the default and the fallback.
4. **Data governance.** Customer queries would go to one more third party. Multi-org customers need this disclosed, and may need a DPA.
5. **Cost isn't the argument.** Triage already runs on the cheap tier with short prompts, so savings per request are small. The case rests on **calibration and latency**, not price.
6. **Token accounting.** `context.AddTokens(Name, _llm.LastTotalTokens)` feeds the token-usage dashboards. ⚠️ We need to confirm Jev reports usage, or else record Jev calls under their own name.

## Proposed integration design (for the bake-off, not yet implemented)

Put a seam under `TriageAgent` so that the label set, the `unclear` policy and the downstream contract (`context.Intent`) stay unchanged:

```csharp
public sealed record IntentDecision(string Intent, double Confidence, IReadOnlyDictionary<string, double> Scores, int TokensUsed);

public interface IIntentClassifier
{
    Task<IntentDecision> ClassifyAsync(AgentContext context, IReadOnlyList<string> labels, CancellationToken ct);
}
```

- `LlmIntentClassifier` holds today's prompt and cleanup logic. It returns `Confidence = 1.0` and `Scores` with only the chosen label, so its behaviour is identical to today's.
- `JevIntentClassifier` is a `HttpClient` adapter. It sends a JSON state (`query`, the last few `history` turns, `hasScreenshot`) and one request containing a Choice question over the five concrete labels. `unclear` is **not** offered as an option: it comes from the confidence policy. The request also carries the atomic Noul questions listed above. It maps `probabilities` into `Scores` and `confidence` into `Confidence`.
- `TriageAgent` applies the threshold policy: `confidence < Triage:MinConfidence` (or `top − second < Triage:MinMargin`) → `unclear`. On a Jev error or timeout (budget around 1 s) it **falls back to `LlmIntentClassifier`** and logs a warning, so Jev can never take down a request.
- Config is `Triage:Classifier = "Llm" | "Jev"` (default `Llm`), plus `Jev:BaseUrl`, `Jev:ApiKey` and `Jev:TimeoutMs`. This follows the existing opt-in pattern used by `CheapChatModel` and `FallbackProvider`.
- Optionally, add `AgentContext.IntentConfidence` for telemetry and the feedback dashboard.

A rough estimate for the seam, adapter, fallback and unit tests is 2–3 days, **after** the official API schema has been verified.

## Recommendation

**Don't adopt yet. Run a time-boxed bake-off**, because Jev's typed Choice with calibrated probabilities matches the shape of the Triage problem closely, and calibrated `unclear` is a real gain. But we can't currently show that triage is a problem, and Jev is an early-access vendor sitting on the critical path.

Order of work:

1. **Build a triage eval first** (useful whatever we decide). Add `Fixtures/triage-fixtures.json` with about 80–120 labelled queries across all six labels. Weight it toward hard cases: bug-vs-question, vague one-liners, greetings with a question inside, and screenshot-mentioning text. Add `RunTriageFixturesAsync` to `SupportForge.Evals` and report per-label accuracy, the `unclear` rate, and p50/p95 latency for the current cheap-tier LLM.
2. **Get Jev access** (waitlist) and verify the official docs: request/response schema, usage reporting, data retention, regions, rate limits and pricing.
3. **Implement the seam and `JevIntentClassifier`** behind `Triage:Classifier`, defaulting to `Llm`.
4. **Compare on the same fixtures.** Adopt Jev as an opt-in classifier only if **all** of these hold:
   - Accuracy is ≥ the LLM baseline (and ≥ 95% overall).
   - Calibration is better: a threshold exists that catches most misclassifications as `unclear` without sending more than about 10% of clear queries there.
   - p95 latency is materially lower than the LLM baseline.
   - Data processing terms are acceptable for multi-org customers.
5. If adopted, follow up with the **Noul** question type for the three verifiers' pass/fail judgments and for `CodeNodeClassifier` (vendored vs hand-written). These are the same shape of problem and get the same calibration benefit.

If step 1 shows the current LLM triage is already accurate and fast enough, stop there. The eval set is still worth keeping.

## Sources

- [TypeSafe docs — Introduction](https://docs.typesafe.ai/introduction) (official; content supplied directly because the host was blocked)
- [KDnuggets — What Everyone Is Getting Wrong About TypeSafe AI's Jev](https://www.kdnuggets.com/what-everyone-is-getting-wrong-about-typesafe-ais-jev)
- [MarkTechPost — TypeSafe AI Releases Jev](https://www.marktechpost.com/2026/09/19/typesafe-ai-releases-jev/)
- [DataCamp — Jev: TypeSafe's System One Model Explained](https://www.datacamp.com/blog/system-one-models-jev)
- [OpenRouter — Jev 1.13](https://openrouter.ai/typesafe/jev-1.13)
- [dev.to — How to Use Jev](https://dev.to/valyuai/how-to-use-jev-a-practical-guide-to-typesafes-system-one-model-g5e)
