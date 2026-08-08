---
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
---

# KbResearcher Tool-Calling Pilot - Plan

## Context

Today's `KbResearcherAgent` is a "direct" agent: the C# code deterministically calls `KbSearchTool.SearchAsync` once with a fixed query/topK, hands the results straight to one raw `ILlmChatClient.CompleteAsync` call, and `KbResearcherVerifier` judges relevance and can force exactly one retry with `topK` bumped 5→10. The LLM never chooses what to search for or how many times — all orchestration lives in the C# workflow graph (`CoordinatorPipeline`).

The brainstorm identified three concrete gaps this misses: multi-part customer questions that need separate searches per sub-topic, paraphrase mismatches where a bigger `topK` on the same wording doesn't help, and wasted retries when the first result was already sufficient. Real tool-calling — letting the model decide which searches to run and when to stop — addresses all three, and also opens the door to adding new tool types (ticketing, live diagnostics) later without hand-wiring a new pipeline node per capability.

Given the outer `CoordinatorPipeline` graph already provides valuable properties (deterministic per-node retries, one OpenTelemetry span per node, prompt-injection defenses via `<retrieved_context>` tags), this is scoped as a **pilot on one agent** rather than a pipeline-wide redesign — validate the pattern before extending it to `CodeAnalyzerAgent` / `VisionAnalyzerAgent`.

## Product Contract

**Scope:** `KbResearcherAgent` only. `CoordinatorPipeline`'s graph (fan-out, fan-in, retry edges), every other specialist agent, and `KbResearcherVerifier`'s position in the flow stay unchanged.

**Behavior change:** `KbResearcherAgent` gets an adaptive multi-query search loop instead of its current single fixed call:
- Can issue separate `KbSearchTool.SearchAsync` calls for distinct sub-questions in one customer message.
- Can reformulate the query wording (not just widen `topK`) when a search looks like a vocabulary/paraphrase mismatch against KB content.
- Can decide it has enough and stop before exhausting the cap, instead of always taking the fixed 1-2 shots today's code takes.
- **Hard cap: 3 search calls per request**, to bound worst-case latency/cost — this is a requirement, not an implementation detail to leave open.

**Guardrail carried forward, not removed:** `KbResearcherVerifier` continues to run after the loop finishes, as an independent second check (per its existing `JudgeAsync` relevance judgment and `FailedRetrying`/`FailedFinal` retry logic in `KbResearcherVerifier.cs:110-114`). The pilot does not remove or bypass this — the tool loop's own stopping decision is not trusted as the sole quality gate.

**Reuse over build-new:** `Microsoft.Extensions.AI` (already a dependency, `SupportForge.Agents.csproj:13`) ships native tool-calling abstractions (`ChatOptions.Tools` / `AIFunction`, auto-invoking chat client wrapping) that work generically across chat clients. The implementation should reuse this rather than hand-rolling a JSON-based ReAct loop against the raw per-provider SDKs. This will require `ILlmChatClient` (`ILlmClient.cs:3-10`) to grow a tool-aware completion path — planning should confirm whether that's a new interface method or a provider-specific adapter, since `AnthropicLlmClient`, `OpenAiLlmClient`, and `BedrockLlmClient` currently only implement plain-text completion.

**Out of scope for this pilot:** CodeAnalyzer/VisionAnalyzer tool-calling, new tool types beyond KB search (ticketing, diagnostics), and any change to the outer pipeline's fan-out/fan-in/retry shape.

## Success Criteria

1. **Eval harness first**: validate the tool-calling version against `SupportForge.Evals`' existing test cases before shipping, comparing answer quality/confidence against the current fixed-retrieval baseline. Mirrors the caution already noted in `DrafterAgent.cs:124-128` about shipping an unvalidated LLM-judged behavior change.
2. **Production telemetry after rollout**: confirm on real traffic via confidence scores, `KbResearcherVerifier` pass rate, and token cost (`AgentContext.AddTokens`) — not eval-set performance alone.

## Key Files

- `SupportForge.Agents/KbResearcherAgent.cs` — becomes the tool-calling loop (replaces its current single `_tool.SearchAsync` call).
- `SupportForge.Agents/Tools/KbSearchTool.cs` — the tool exposed to the loop; signature (`SearchAsync(projectId, query, topK, ct)`) is likely reusable as-is.
- `SupportForge.Agents/ILlmClient.cs` — `ILlmChatClient` needs a tool-aware completion path.
- `SupportForge.Agents/AnthropicLlmClient.cs`, `OpenAiLlmClient.cs`, `BedrockLlmClient.cs` — provider clients that would need to support the new tool-aware path (or be adapted to `Microsoft.Extensions.AI`'s `IChatClient`).
- `SupportForge.Agents/KbResearcherVerifier.cs` — unchanged in position/logic; stays the outer safety net.
- `SupportForge.Agents/CoordinatorPipeline.cs` — unchanged; confirms this pilot doesn't touch the graph.
- `SupportForge.Evals` — where pre-rollout validation runs.

## Outstanding Questions for Planning

- Does `ILlmChatClient` grow a new method (e.g. a tool-aware completion) or does the pilot bypass it and call `Microsoft.Extensions.AI`'s `IChatClient` abstraction directly for just this agent?
- Which provider(s) does the pilot need to support tool-calling for first — just whichever is configured as default today, or all three (Anthropic/OpenAI/Bedrock)?
- Does the 3-call cap need to be configurable (like `Drafter:GroundednessCheckEnabled`'s pattern) or is a fixed constant acceptable for a pilot?

## Verification

- Run `SupportForge.Evals` against both the current `KbResearcherAgent` and the tool-calling version over the same test set; compare confidence/quality before merging.
- Add/extend unit tests for `KbResearcherAgent` covering: single-search case behaves like today, multi-part question triggers multiple searches, cap-hit case stops at 3 calls without erroring.
- Manually exercise a known multi-part support question through the full pipeline (`CoordinatorPipeline.RunAsync`) and confirm `KbResearcherVerifier` still gates a genuinely irrelevant result to `FailedFinal`/cleared snippets as it does today.
