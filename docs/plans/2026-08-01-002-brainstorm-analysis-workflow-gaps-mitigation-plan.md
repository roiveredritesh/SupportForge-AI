---
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
---

# Analysis-Workflow Gaps Mitigation - Plan

## Goal Capsule

**Objective:** `docs/analysis-workflow.md` (an AI-generated architecture audit, dated 2026-08-01, currently untracked in the `feat/kb-rag-graphify-foundation` worktree) is stale — it describes an earlier snapshot of SupportForge. Before it drives any work, its claims were verified against the current `master` codebase. This plan corrects the record and scopes the mitigations that are still real.

**Product authority:** engineering-internal (reliability/observability/security hardening), no external product-shape decision.

**Open blockers:** none — this is a requirements-only plan ready for `ce-plan` to enrich per workstream.

## Verification Summary

The source document is **substantially stale**. Corrections, by section:

| Claim in doc | Verdict | Actual state |
|---|---|---|
| React 18 | REFUTED | `frontend/package.json` pins React 19.2.7 |
| 4 routes incl. "Statistics" | REFUTED | 3 routes exist: `/`, `/query`, `/admin` — no Statistics page |
| "Lack of component-level unit tests" | REFUTED | 7 Vitest/Testing-Library test files exist under `frontend/src` |
| "No end-to-end tests" | CONFIRMED | No Cypress/Playwright |
| API "only 2 endpoints" | PARTIALLY TRUE | `ChatController` has both, but `FeedbackController`, `FreshnessController`, `IngestionController`, `ProjectsController`, `ConversationsController` also exist — doc's surface description is misleadingly narrow |
| No auth / no rate limiting | CONFIRMED | `Program.cs` has no `AddAuthentication`/`AddJwtBearer`/`RateLimiter` |
| No request validation | PARTIALLY TRUE | Ownership checks exist (`ChatController.cs:74-91`); no size/format limits on `Query`/screenshot |
| Agent order hard-coded, QueryStream duplicates chain | PARTIALLY TRUE | `CoordinatorPipeline` exists; `QueryStream` re-runs agents via a shared `RunWithVerificationAsync` helper, not blind copy-paste, but still a second orchestration path |
| Ingestion "no health check, no retry" | CONFIRMED | Catch-and-continue loop, no backoff/retry policy |
| **GraphifyCliRunner "not present currently"** | **REFUTED — materially wrong** | Fully built at `backend/SupportForge.Ingestion/Graphify/GraphifyCliRunner.cs` (170 lines, `ILogger`, concurrency gate) |
| Testing "no integration tests" | REFUTED | `backend/SupportForge.Api.Tests/Integration/EndToEndQueryTests.cs` exists |
| No Docker, no CI/CD | CONFIRMED | No `Dockerfile`, no `.github/workflows/` |
| "Only TriageAgent logs" | PARTIALLY TRUE | All 5 specialist agents (`SupportForge.Agents/*`) have zero `ILogger` usage; ingestion *does* log — doc's specific claim about agents holds even though its general framing doesn't |
| No tracing/OpenTelemetry | CONFIRMED | No OTel packages found |
| `/health` returns bare 200 | CONFIRMED | `Program.cs:86`, no dependency checks |
| No Polly/circuit-breaker anywhere | CONFIRMED | Repo-wide, zero matches |
| Secrets "hard-coded in config" | REFUTED | `appsettings.json` has blank `ApiKey` placeholders (field names only, no values), read via `IConfiguration`/env. Repo-wide scan for key-shaped strings (`sk-`, `AKIA`, `Bearer `) across all `*.json` found no real secrets, only the field names and an unrelated npm-registry match in `package-lock.json` |
| No Polly/circuit-breaker | CONFIRMED (re-verified) | Checked every `.csproj` under `backend/` for `Polly`/`OpenTelemetry`/`Resilience` package references — zero matches, not just a source grep |
| "No integration tests" | REFUTED (detail added) | `EndToEndQueryTests.cs` is a real `WebApplicationFactory<Program>`-based integration test hitting `/api/projects`, `/api/ingestion/trigger`, `/api/chat/query` end-to-end — but it's `[SkippableFact]`, gated on `OpenAI__ApiKey` + a live Chroma instance, so it doesn't run in a default CI-less environment. It exists and is genuine, but currently only runs when someone has live credentials locally |
| `docs/agentic-pipeline.md` staleness (source doc §8) | CONFIRMED | Read the file directly: it documents the same outdated 5-agent-only flow (`Triage -> KbResearcher -> CodeAnalyzer -> VisionAnalyzer -> Drafter`) with no mention of `Verifier`/retry components anywhere in it — the canonical pipeline doc is stale in the same way the audit doc is |

**Missed by the source document entirely (real gaps it never saw):**
- A **Verifier/retry layer** (`KbResearcherVerifier`, `CodeAnalyzerVerifier`, `VisionAnalyzerVerifier`) sits alongside the 5 specialist agents. Any mitigation plan that still says "5 sequential agents" is planning against the wrong architecture — it's actually 8 components (5 specialists + 3 verifiers + coordinator).
- **Three LLM provider clients** (`OpenAiLlmClient`, `AnthropicLlmClient`, `BedrockLlmClient`) share **no resilience wrapper** — more specific and higher-value than the doc's generic "wrap LLM calls in Polly."

## Product Contract

### Problem
The architecture audit that's supposed to seed a hardening effort is wrong often enough (Graphify status, test coverage, React version, route count) that acting on it as-written would waste effort re-verifying facts and miss the two gaps it never saw (agent logging, LLM-client resilience, Verifier layer invisibility). Confirmed real gaps still stand: no auth/rate-limiting, no tracing, no Docker/CI, no retry/circuit-breaker anywhere, agents don't log, ingestion has no retry policy, health check is a bare 200.

### In Scope
Corrective mitigation workstreams, each independently deliverable, derived only from **CONFIRMED** or **PARTIALLY TRUE** gaps above:

1. **Agent observability** — add `ILogger` to all 5 specialist agents + 3 verifiers (currently zero logging in `SupportForge.Agents/*`); log per-step entry with agent name and outcome.
2. **Resilience for LLM clients** — wrap `OpenAiLlmClient`, `AnthropicLlmClient`, `BedrockLlmClient` with a shared Polly retry/circuit-breaker policy (not a generic "wrap LLM calls" — specifically these three, since they currently duplicate no shared resilience code).
3. **Ingestion retry policy** — `IngestionBackgroundService` currently catches and continues with no backoff; add a retry/backoff policy so transient crawl failures don't silently skip a cycle.
4. **AuthN/rate-limiting** — add JWT bearer auth and `Microsoft.AspNetCore.RateLimiter` middleware; currently absent from `Program.cs`.
5. **Input validation hardening** — extend the existing ownership checks (`ChatController.cs:74-91`) with size/format limits on `Query` length and screenshot payload size.
6. **Deep health check** — replace the bare `/health` 200 with dependency checks (vector store, LLM connectivity, Graphify availability).
7. **Tracing** — introduce OpenTelemetry spans across the agent pipeline (currently none).
8. **Containerization/CI** — add a `Dockerfile` and a CI workflow (build, test, package); currently absent.
9. **Orchestration consolidation** — `ChatController.QueryStream` re-runs agents via `RunWithVerificationAsync` rather than delegating to `CoordinatorPipeline`; evaluate consolidating to one orchestration path.
10. **Pipeline doc correction** — `docs/agentic-pipeline.md` documents only the 5-agent flow with no mention of the Verifier layer; update it to reflect the actual 8-component architecture (5 specialists + 3 verifiers + coordinator).

Note: item 8 (containerization/CI) also unblocks `EndToEndQueryTests.cs` — it's a genuine integration test but is `[SkippableFact]`-gated on live `OpenAI__ApiKey` + a running Chroma instance, so it never runs today outside a manually-configured local environment. A CI workflow with test-stub credentials would let it actually execute on every change.

### Out of Scope
- Anything the source document flagged that verification refuted: rebuilding Graphify integration (already exists), adding component/integration tests (already exist — gap is only e2e/load testing, which stays a real but separate, lower-priority item), fixing "hardcoded secrets" (not hardcoded).
- Documentation-generation tooling (docfx/Sailfish) from the source doc's §8 — no evidence gathered on doc staleness beyond this plan itself; not a verified gap.
- Multi-tenant auth design, key rotation policy, and the specific auth provider choice — those are implementation decisions for whichever workstream(s) are picked up next in `ce-plan`.

### Key Decisions
- Correct the architectural model before planning against it: **8-component pipeline** (5 specialist agents + 3 verifiers + `CoordinatorPipeline`), not the 5-agent model the source document used.
- Treat "wrap LLM calls in Polly" as three concrete targets (`OpenAiLlmClient`, `AnthropicLlmClient`, `BedrockLlmClient`), not a vague aspiration — this is the gap the source document's own generic recommendation would have under-scoped.
- Each of the 9 in-scope items is an independently deliverable workstream; no single "resilience layer" PR needs to bundle all of them.

### Assumptions
- E2E/load testing (browser-level, via Cypress/Playwright) and documentation-automation tooling (docfx/Sailfish) remain real gaps per the source document but are deliberately out of scope for this mitigation plan; they can be their own follow-up plan if prioritized. This is a scoping choice, not an unverified claim.
- Remaining PARTIALLY TRUE items (API surface description, agent-order duplication, request validation) reflect genuine nuance captured in the verification table above, not unresolved uncertainty — no further checking is pending on them.

All other items originally listed as assumptions (secret-management scope, Polly/OTel package-level check, integration test's actual run conditions, `docs/agentic-pipeline.md` staleness) have been independently re-verified and folded into the Verification Summary table above as confirmed facts.

## Outstanding Questions
- Which of the 9 workstreams should `ce-plan` take first? No priority ordering was requested or set in this pass — this plan is intentionally unordered so the next planning step can sequence by risk/cost.
- Auth provider choice (ASP.NET Identity vs. external OIDC provider) is unresolved — deferred to whichever workstream picks up item 4.
