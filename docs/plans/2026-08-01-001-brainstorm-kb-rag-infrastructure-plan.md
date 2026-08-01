---
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-brainstorm
execution: code
---

# KB/RAG Infrastructure for Multi-Repo, Multi-Provider Agent Context - Plan

## Goal Capsule
- **Objective**: design and implement, as one integrated system, cross-dependency repo coverage for RCA, clone-time KB pregeneration, pluggable external KB sources (Confluence/PDF/MD/TXT/websites/GitHub repos), source refresh, and context-rot/caching/logging/observability for the Support-pipeline agent framework — sequenced by dependency order, not urgency.
- **Product authority**: `ce-brainstorm` session (this file); no separate STRATEGY.md consulted.
- **Product Contract preservation**: unchanged in meaning; the Context section below was corrected during the 2026-08-01 verification pass (multi-repo ingestion is already wired — see Appendix finding #10), which is a factual correction, not a scope change. No R-ID was added or removed by that pass.

## Verification Pass (2026-08-01)
This plan was re-verified against the repository at commit `bfcbad0` and against the installed graphify CLI (`graphify 0.9.9`). Twelve gaps were found and resolved inline; the paragraph-level record is in **Appendix: Verification Findings**. Claims that survived verification unchanged are not re-listed there.

---

## Product Contract

### Context
Support-pipeline's agent pipeline (`Triage -> KbResearcher/CodeAnalyzer/VisionAnalyzer (parallel) -> Drafter`, see `backend/SupportForge.Agents/CoordinatorPipeline.cs`) ingests code into a vector store per project, sourced from `Project.Repos`, plus local documents/PDFs via `KbSourceConfig` (Confluence is an unimplemented enum member; no web or GitHub-doc connectors exist). There is no refresh mechanism, no clone-time summarization, and effectively no logging or observability: `ILogger` appears in exactly one file (`backend/SupportForge.Ingestion/IngestionBackgroundService.cs`), and no OpenTelemetry package is referenced by any project.

Multi-repo ingestion is **already wired end to end**: `CodeIngestionJobFactory.CreateJobs` fans out one `CodeIngestionJob` per entry in `project.Repos`, and `FreshnessCalculator` tracks staleness per repo. The real constraints are upstream and adjacent:
- The Admin onboarding flow and `docs/deployment/configuration-guide.md` describe adding **one** GitHub repo per project.
- `DocumentIngestionJobFactory` resolves KB document folders against `project.Repos.FirstOrDefault()` (an explicit `ponytail:` shortcut) — a first-repo assumption that becomes wrong the moment a project has peer repos.
- `GitRepoSyncService` authenticates every clone with a single global `GitHub:Token`; `GitHubRepoConfig.AccessTokenSecretName` is modeled but never read.
- No Dockerfile or container definition exists anywhere in the repo, so there is no established runtime story for the Python + `graphifyy` dependency this plan requires on the backend host (Appendix finding #12).

### Requirements
- **R1**: Cross-dependency repos are added via manual config (`Project.Repos`), never auto-detected from manifests.
- **R2**: Refresh supports all three trigger modes (webhook, scheduled polling, manual), independently configurable per source.
- **R3**: Cross-repo RCA is modeled as a dependency graph (peers), not a flat `Primary`/`Dependency` role — no repo is inherently primary in a microservice project.
- **R4**: Model/provider flexibility is a first-class goal across **two independent surfaces**: the app's own `ILlmClient` seam (chat/embeddings/vision) and graphify's own `extract --backend` selection — for OpenAI, Anthropic, AWS Bedrock, Azure, NVIDIA NIM, and custom-hosted (OpenAI-compatible) models.
- **R5**: graphify is the KB engine, not a bespoke chunk-and-embed pipeline. AST-based code extraction needs no LLM call; `IVectorStoreService`/Chroma is demoted to an optional secondary index for prose-similarity search only.
- **R6**: The backend integrates graphify via its headless CLI (process spawn against the installed `graphify` executable), never the `/graphify` agent skill, which only runs inside an agent host.
- **R7**: Onboarding, document ingestion, and credential resolution must work correctly for a project with more than one repo (fix the first-repo assumption and the single global GitHub token).
- **R8**: The system must have working logging, tracing/metrics observability, context-rot mitigation (token-budgeted retrieval), prompt caching, and per-run cost accounting (both LLM tokens and graphify's own build cost) — none of which exist today beyond one `ILogger` usage.

### Key Decisions
- Cross-dependency repos are added via **manual config** (`Project.Repos`), not auto-detected from manifests — v1 keeps it explicit and predictable. *(Governs R1)*
- Refresh must support **all three trigger modes** (webhook, scheduled polling, manual), configurable **per source**, so the admin picks what fits each connector. *(Governs R2)*
- **Microservices break "Primary vs Dependency."** A binary role doesn't fit a peer-service architecture where no repo is inherently primary. Phase 4 is designed around graphify's own dependency graph, not a hand-built role field. *(Governs R3)*
- **Model/provider flexibility is a core project goal**, not an afterthought — must support OpenAI, Anthropic, AWS Bedrock, Azure, NVIDIA NIM, and custom-hosted models. Verification showed this goal has **two** provider surfaces, not one: the app's own `Llm:*` config, and graphify's independent `extract --backend` selection. Both must be satisfied. *(Governs R4)*
- **Use graphify as the KB engine**, not a bespoke chunk-and-embed pipeline. AST-based code extraction needs no LLM call, it re-extracts only changed code on `update`, and `query`/`path`/`explain`/`affected` give token-budgeted, targeted retrieval instead of raw chunk dumps. The vector store is demoted to an optional secondary index. *(Governs R5)*
- **Integrate graphify via its headless CLI, not its agent skill.** The .NET backend must shell out to the installed `graphify` executable, whose surface is subcommand-based (`extract`, `update`, `query`, `path`, `explain`, `affected`, `add`, `merge-graphs`, `global`, `check-update`, `benchmark`). Every command in this plan is written in CLI form. *(Governs R6)*

---

## Planning Contract

### Key Technical Decisions

- **KTD1 — Split `ILlmClient` by capability** *(session-settled: user-directed — chosen over keeping one interface with capability flags: Anthropic has no embeddings API, so a single interface with one `Llm:Provider` flag makes "Anthropic chat + any-provider embeddings" impossible to express, not merely awkward)*. Replace the single `ILlmClient` with three seams — a chat/streaming client, an embeddings client, and a vision-capable chat client — each independently selected by config (`Llm:Provider`, `Embeddings:Provider`, and a vision-capability declaration on the configured chat provider). Resolves **D1**. *(Governs R4)*
- **KTD2 — Front Bedrock/Azure with an OpenAI-compatible gateway for graphify** *(session-settled: user-directed — chosen over restricting semantic extraction to graphify-native backends or skipping semantic extraction on Bedrock/Azure: it costs no graphify code changes and keeps the "any provider" promise honest for docs/PDF/website ingestion)*. Document and support standing up a LiteLLM-style OpenAI-compatible proxy in front of Bedrock/Azure models, and point `graphify extract --backend openai` at that proxy's URL via environment variables. Resolves **D2**. *(Governs R4)*
- **KTD3 — graphify backend config is derived, not separately entered.** Wherever the app's configured chat provider has an OpenAI-compatible or Anthropic-compatible wire format (NIM, vLLM, custom-hosted, or the KTD2 gateway), the backend derives graphify's `OPENAI_BASE_URL`/`OPENAI_MODEL` or `ANTHROPIC_BASE_URL`/`ANTHROPIC_MODEL` environment from the same admin-facing config, so an operator configures a provider once. When the two surfaces cannot be reconciled (e.g. no gateway configured for a Bedrock-only deployment and semantic extraction is required), the app fails loudly at configuration/startup time with a specific message, not silently at first ingestion. *(Governs R4)*
- **KTD4 — Global graph over one-shot merges for persistent multi-repo state.** `graphify global add <graph.json> --as <tag>` (with `global list`/`global remove`) is the primary mechanism for cross-repo graphs whose repo set changes over time; `merge-graphs` remains available for one-shot comparisons. Every cross-repo query call passes an explicit `--graph`. `graphify diagnose multigraph` runs as a build-time gate before any merged/global graph is trusted for RCA. *(Governs R3, R5)*
- **KTD5 — Refresh splits into two classes by cost, not one command.** Code refresh runs `graphify update` (free, no LLM) on every push-class trigger. Docs/Confluence/website refresh requires `graphify extract`/`add` (LLM-backed) and is gated by `graphify check-update`, run on its own budgeted schedule — never attached directly to a code webhook. *(Governs R2)*
- **KTD6 — Process-spawn plumbing is first-class infrastructure, not incidental.** Because no container/runtime story exists yet (Appendix #12), a dedicated component owns invoking the `graphify` CLI as a subprocess: working directory, timeouts, concurrency limits (bounding parallel `extract`/`update` runs against CPU/LLM-rate-limit pressure), non-zero-exit handling, and stdout/stderr capture for logging. This is built once in Phase 0/1 and reused by every later phase rather than each call site reinventing subprocess handling. The runner invokes `graphify` via an argument array (never shell-string concatenation) and validates/normalizes any ticket-derived or user-supplied string (query text, URLs, entry-point names) before it becomes a CLI argument, since these values flow from external/untrusted sources by Phase 4. *(Governs R6)*
- **KTD7 — Name the secret store for per-repo and third-party credentials.** `GitHubRepoConfig.AccessTokenSecretName` and the new Confluence API credential both resolve through one named secret-retrieval mechanism (e.g. a minimal `ISecretResolver` reading from the platform's configured secret store — Key Vault/Secrets Manager in cloud deployments, environment variables or `dotnet user-secrets` for local/dev), rather than being wired ad hoc per feature. Resolved secret values are never written to logs or subprocess stdout/stderr capture (see U4, U11). *(Governs R7, R8)*

### Deferred to Implementation
- Exact Bedrock connector library (AWS SDK direct vs. a community `Microsoft.Extensions.AI` connector) — resolve once implementation starts by checking current package availability, since this changes faster than the plan should track.
- Exact per-repo guardrail policy schema for the no-code-exposure filter (`docs/plans/2026-07-29-001-feat-rca-answers-no-code-exposure-plan.md`) applied per repo/service.
- How the entry-point resolver scores ambiguous ticket-to-service matches (threshold for "confident enough" vs. falling back to whole-graph query).
- Whether graphify runs in-process on the API host or as a separate worker/container — depends on operational constraints not yet decided; KTD6's process-spawn abstraction must not assume either.

---

## Implementation Units

### Phase 0 — Provider flexibility (parallel to Phase 1)

### U1. Split the LLM client seam by capability
- **Goal**: Replace `ILlmClient` with capability-scoped interfaces so chat, embeddings, and vision can each be satisfied by a different provider.
- **Requirements**: R4 (KTD1)
- **Dependencies**: none
- **Files**:
  - `backend/SupportForge.Agents/ILlmClient.cs` — replace with `IChatClient` (or rename existing usages), `IEmbeddingClient`, and a vision-capability contract
  - `backend/SupportForge.Agents/OpenAiLlmClient.cs` — split into per-capability implementations (or an adapter implementing all three, since it already satisfies all of them)
  - `backend/SupportForge.Agents/Tools/CodeSearchTool.cs`, `KbSearchTool.cs` — update to depend on the embeddings seam instead of `ILlmClient`
  - `backend/SupportForge.Api/Program.cs` — DI registration for the three seams
  - Test files alongside each new/changed class (mirror existing test layout in `backend/SupportForge.Api.Tests/`)
- **Approach**:
  1. Introduce `IChatClient` (CompleteAsync/StreamCompleteAsync/AnalyzeImageAsync + `LastTotalTokens`) and `IEmbeddingClient` (EmbedAsync + `LastTotalTokens`) as the two mandatory seams; add a `SupportsVision` capability flag readable from config or the client itself.
  2. Adapt `OpenAiLlmClient` to implement both (it already can — OpenAI/NIM support all three), so this unit is additive, not a rewrite of working code.
  3. Update every consumer (`CodeSearchTool`, `KbSearchTool`, ingestion jobs, agents) to take the narrower interface they actually need.
  4. `CoordinatorPipeline` (or `VisionAnalyzerAgent`) checks the vision capability flag and skips/short-circuits `VisionAnalyzerAgent` with a clear reason when the configured chat provider lacks vision, instead of failing at request time.
- **Patterns to follow**: existing `ILlmClient`/`OpenAiLlmClient` split between interface and `Microsoft.Extensions.AI`-backed implementation; existing DI registration style in `Program.cs:51`.
- **Test scenarios**:
  - Happy path: a chat-only call against the new `IChatClient` returns text and populates `LastTotalTokens`.
  - Happy path: an embeddings call against `IEmbeddingClient` returns a vector and populates `LastTotalTokens` independently of chat usage.
  - Edge case: a configured chat provider with `SupportsVision = false` causes `CoordinatorPipeline` to skip `VisionAnalyzerAgent` and produce a clear "vision unsupported" status rather than throwing.
  - Integration: `CodeSearchTool`/`KbSearchTool` still resolve embeddings correctly after the interface split (existing tests in `CodeAnalyzerAgentTests.cs`/`KbResearcherAgentTests.cs` continue to pass).
- **Verification**: existing agent test suites pass unchanged in behavior; a manual run can configure chat and embeddings against two different provider sections and complete one query end to end.

### U2. Add Anthropic, Bedrock, and Azure provider implementations
- **Goal**: Satisfy R4's provider list for the app's own LLM seam, now that capability splitting (U1) makes each provider only need to implement what it supports.
- **Requirements**: R4
- **Dependencies**: U1
- **Files**:
  - `backend/SupportForge.Agents/AnthropicLlmClient.cs` (new) — chat + vision only
  - `backend/SupportForge.Agents/BedrockLlmClient.cs` (new) — chat/embeddings per Bedrock's wire format and SigV4 auth
  - Azure wiring inside `Program.cs`/a new `AzureLlmClientFactory` using `Microsoft.Extensions.AI`'s Azure `IChatClient`/`IEmbeddingGenerator` connector
  - `backend/SupportForge.Api/appsettings.json` — new provider config sections
  - `docs/deployment/configuration-guide.md` — document each new `Llm:Provider` value and its required config (reconcile with the file's existing uncommitted local modifications first — see Critical Files)
- **Approach**:
  1. Azure: thinnest addition — wire the official connector as a new `Llm:Provider` branch (deployment-based URL + `api-key` auth).
  2. Anthropic: new chat-and-vision-only implementation of `IChatClient`, since Anthropic's message format, system-prompt-as-top-level-field, and streaming events all differ from the OpenAI-compatible shape `OpenAiLlmClient` already handles.
  3. Bedrock: new implementation of `IChatClient`/`IEmbeddingClient` using whichever SDK path is available at implementation time (Deferred to Implementation).
  4. Every new implementation populates `LastTotalTokens` consistently, since Phase 5 (U13) depends on it for cost observability.
- **Patterns to follow**: `backend/SupportForge.Agents/OpenAiLlmClient.cs` for the shape of an `ILlmClient`-family implementation and how `LastTotalTokens` is tracked.
- **Test scenarios**:
  - Happy path: Anthropic chat completion returns text with correct token accounting.
  - Happy path: Bedrock chat and embeddings both succeed against the new client.
  - Happy path: Azure OpenAI chat completes using the official connector.
  - Edge case: Anthropic client's `EmbedAsync` is not exposed (no embeddings method to call) — confirms the capability split (U1) actually prevents the previously-impossible configuration at the type level, not just by convention.
  - Error path: Bedrock SigV4 auth failure surfaces a clear error rather than a generic HTTP exception.
- **Verification**: for each new provider, one real (or recorded/mocked) chat call completes and, where applicable, one embeddings call completes, with `LastTotalTokens` non-zero.

### U3. Derive graphify's backend environment from the app's provider config
- **Goal**: Satisfy R4's second provider surface (graphify's own `--backend`) without requiring operators to configure providers twice, and implement the Bedrock/Azure gateway path from KTD2.
- **Requirements**: R4 (KTD2, KTD3)
- **Dependencies**: U1, U2, U4 (process-spawn plumbing)
- **Files**:
  - New config-derivation component (e.g. `backend/SupportForge.Ingestion/Graphify/GraphifyBackendResolver.cs`)
  - `backend/SupportForge.Api/Program.cs` — startup-time validation
  - `docs/deployment/configuration-guide.md` — document the OpenAI-compatible-gateway requirement for Bedrock/Azure and the derived environment variables
- **Approach**:
  1. When the configured chat provider is OpenAI-compatible (OpenAI, NIM, custom-hosted, or a KTD2 gateway) or Anthropic-native, derive `OPENAI_BASE_URL`/`OPENAI_MODEL` or `ANTHROPIC_BASE_URL`/`ANTHROPIC_MODEL` for the graphify subprocess environment automatically.
  2. When the configured provider is Bedrock or Azure with no gateway URL configured, fail startup validation with a specific, actionable message (per KTD3) rather than letting the first `graphify extract` call fail obscurely. When a gateway URL is configured, startup validation also requires it to be an HTTPS endpoint and requires an API key/credential for it — an HTTP or unauthenticated gateway URL fails validation the same way a missing one does, since the gateway is a trust boundary graphify's output is taken on faith from.
  3. Document the recommended LiteLLM-style gateway setup for Bedrock/Azure-only deployments per KTD2.
- **Patterns to follow**: existing `Program.cs` pattern of reading `Llm:Provider` and selecting a config section.
- **Test scenarios**:
  - Happy path: chat provider = NIM (OpenAI-compatible) → graphify backend environment is derived correctly and `graphify extract --backend openai` succeeds against a test corpus.
  - Happy path: chat provider = Anthropic → graphify backend environment derives `--backend claude` correctly.
  - Error path: chat provider = Bedrock with no gateway URL configured → startup fails with a message naming the missing gateway config, not a runtime graphify failure.
  - Edge case: chat provider = Bedrock with a gateway URL configured → graphify backend environment derives `--backend openai` pointed at the gateway.
- **Verification**: startup succeeds or fails deterministically per the matrix above; a manual `graphify extract` run against a small test corpus succeeds using the derived environment for at least one OpenAI-compatible and one Anthropic-compatible configuration.

### Phase 1 — Unify KB sources through graphify

### U4. Process-spawn plumbing for the graphify CLI
- **Goal**: Give every later unit a single, reliable way to invoke `graphify` as a subprocess (KTD6), instead of each ingestion path reinventing process handling.
- **Requirements**: R6 (KTD6)
- **Dependencies**: none (foundational; can run parallel to U1-U3)
- **Files**:
  - New `backend/SupportForge.Ingestion/Graphify/GraphifyCliRunner.cs` (or similar) — subprocess wrapper
  - `backend/SupportForge.Api/Program.cs` — DI registration
- **Approach**:
  1. Wrap `graphify <subcommand> <args>` invocation with: working directory set to the repo/corpus cache path, a configurable timeout, a bounded concurrency semaphore (parallel `extract`/`update` runs contend for CPU and LLM rate limits), captured stdout/stderr routed to `ILogger` (redacted per U11's redaction rule before logging — the subprocess environment can include provider base URLs/keys per U3), and explicit non-zero-exit-code handling that surfaces the captured stderr in the thrown exception.
  2. Invoke the subprocess via an argument array (`ProcessStartInfo.ArgumentList`), never a concatenated shell command string, so no argument requires shell-escaping. Validate/normalize any value sourced from ticket text, URLs, or other external input (U6's `graphify add <url>`, U10's `graphify query`/`path` arguments) before passing it as a CLI argument — reject or strip values containing null bytes, newlines, or leading-hyphen strings that could be misread as flags.
  3. Confirm `graphify-out/` location relative to the existing repo cache root (`backend/SupportForge.Ingestion/Code/GitRepoSyncService.cs` clone path convention) so later units have one settled answer instead of re-deciding it.
  4. Assumes Python + `graphifyy` are present on the backend host; document this as an operational prerequisite (Deferred to Implementation: containerization strategy).
- **Patterns to follow**: `backend/SupportForge.Ingestion/Code/GitRepoSyncService.cs` for how existing external-process-adjacent work (git operations) is wrapped and logged.
- **Test scenarios**:
  - Happy path: running a benign subcommand (e.g. `graphify --version` or `check-update` against an empty corpus) returns success and captured output.
  - Error path: a non-existent path or invalid subcommand produces a specific exception carrying stderr content, not a bare non-zero exit code.
  - Edge case: exceeding the configured timeout cancels the subprocess and reports a timeout-specific error.
  - Integration: two concurrent invocations beyond the concurrency limit queue rather than both running simultaneously.
  - Security: a ticket-derived or URL input containing shell metacharacters or a leading-hyphen string is passed through as a literal argument value (via the argument-array invocation) rather than being interpreted as a shell command or an unintended CLI flag.
- **Verification**: unit tests around the runner using a stub/fake process where feasible; one manual end-to-end run against the real installed `graphify` CLI confirming output capture and working-directory behavior.

### U5. Ingest code repos through graphify instead of the vector store
- **Goal**: Replace `CodeIngestionJob`'s chunk-and-embed path with `graphify extract`, the single largest token saving in the plan (R5).
- **Requirements**: R5, R6
- **Dependencies**: U4
- **Files**:
  - `backend/SupportForge.Ingestion/Code/CodeIngestionJob.cs` — replace the `DocumentChunker` + `EmbedAsync` loop with a `graphify extract <clonedPath>` call via `GraphifyCliRunner`
  - `backend/SupportForge.Ingestion/Code/CodeIngestionJobFactory.cs` — no structural change (already fans out per repo); confirm the graphify invocation slots into the existing per-repo job
  - Test updates in `backend/SupportForge.Api.Tests/Ingestion/IngestionJobFactoryTests.cs`
- **Approach**:
  1. After `GitRepoSyncService.CloneOrPull`, invoke `graphify extract <localCachePath> --no-cluster` (no `--backend` needed for a code-only corpus — AST extraction requires no LLM, and `--no-cluster` keeps this specific call LLM-free by skipping the separate community-labelling step; see U7, which runs clustering as its own, explicitly budgeted step on top of this base extraction rather than folding it into every ingestion call).
  2. Keep the existing `FreshnessCalculator`/`LastSyncedAt` update logic; only the extraction mechanism changes.
  3. Vector store upsert (`IVectorStoreService`) is no longer the primary path for code; leave the vector store call in place only if a secondary prose-similarity index is still wanted for code (per R5 — likely not needed for code specifically, since graphify's `query`/`path`/`explain` replace it).
- **Patterns to follow**: existing `CodeIngestionJob.RunAsync` structure for clone → process → persist ordering.
- **Test scenarios**:
  - Happy path: ingesting a small test repo with `--no-cluster` produces a `graphify-out/graph.json` with code nodes and zero LLM calls (AST-only).
  - Happy path: `LastSyncedAt` is updated on the project's matching `GitHubRepoConfig` entry after a successful extraction, same as today.
  - Error path: `graphify extract` failure (non-zero exit) is caught and surfaced without corrupting `LastSyncedAt` (it should not be updated on failure).
  - Regression: existing `CodeIngestionJobTests`-equivalent coverage still passes with the new extraction mechanism substituted for chunk/embed.
- **Verification**: Acceptance Example — code-only extraction with `--no-cluster` over a test repo produces `graphify-out/graph.json` with code nodes and makes zero LLM calls. (Community labelling, which does cost tokens, is verified separately as part of U7 — see that unit's Verification.)

### U6. Ingest documents, Confluence, and websites through graphify; fix the first-repo assumption
- **Goal**: Replace bespoke document chunking with graphify extraction for docs/PDF/MD/TXT, add a Confluence-to-markdown fetch step, support `graphify add <url>` for websites, and remove `DocumentIngestionJobFactory`'s `Repos.FirstOrDefault()` shortcut (R7 — a Phase 4 prerequisite, done here since it's the same code path).
- **Requirements**: R5, R6, R7
- **Dependencies**: U4
- **Files**:
  - `backend/SupportForge.Ingestion/Documents/DocumentIngestionJobFactory.cs` — remove `Repos.FirstOrDefault()`; resolve the target repo/path explicitly from `KbSourceConfig` (extend it with an explicit repo/path reference)
  - `backend/SupportForge.Ingestion/Documents/DocumentIngestionJob.cs` — replace `DocumentChunker` + embed loop with `graphify extract`/`graphify add`
  - `backend/SupportForge.Core/Entities/KbSourceConfig.cs` — extend `KbSourceType` (add `Website`), add an explicit repo/path association field
  - New thin Confluence fetch step (Confluence REST API → markdown file into the corpus folder) — new file under `backend/SupportForge.Ingestion/Documents/`
  - Test updates in `backend/SupportForge.Api.Tests/Ingestion/DocumentIngestionJobPdfTests.cs`, `IngestionJobFactoryTests.cs`
- **Approach**:
  1. Add an explicit repo/path association to `KbSourceConfig` so a KB source declares which repo (or an absolute path) it belongs to; `DocumentIngestionJobFactory` reads that instead of `Repos.FirstOrDefault()`.
  2. For `Documents` sources: `graphify extract <folder>` in place of `DocumentChunker` + `EmbedAsync`.
  3. For a new `Website`/URL source type: `graphify add <url>`.
  4. For `Confluence`: fetch the page via the Confluence REST API, write it as markdown into the corpus folder, then extract it like any other doc — this remains the one bespoke connector. The Confluence API credential resolves through the same `ISecretResolver` mechanism as GitHub per-repo tokens (KTD7), not a separate ad hoc lookup; a 401/403 from Confluence fails that source's ingestion with a clear error rather than retrying with a stale credential.
  5. This path (unlike code) consumes LLM tokens via graphify's semantic extraction — no attempt to make it free.
- **Patterns to follow**: `backend/SupportForge.Ingestion/Documents/DocumentIngestionJobFactory.cs`'s existing factory shape; the `ponytail:` comment marking the shortcut being removed.
- **Test scenarios**:
  - Happy path: a project with two repos and a KB source explicitly associated with the second repo resolves correctly (previously would have silently resolved against the first).
  - Happy path: a local PDF/MD/TXT corpus ingests via `graphify extract` and produces graph nodes with correct `source_location`.
  - Happy path: `graphify add <url>` for a website source folds the page into the graph.
  - Integration: a Confluence source fetches a page, writes it to markdown, and the subsequent `graphify extract` call picks it up.
  - Error path: an unresolvable repo/path association on a `KbSourceConfig` fails ingestion for that source with a clear error instead of silently defaulting to the first repo.
- **Verification**: Acceptance Example — `graphify extract` over a test repo plus a Confluence-derived markdown file, and `graphify add <url>` for a website, produce a `graphify-out/graph.json` with nodes from all three sources and correct `source_location`.

### Phase 2 — Clone-time KB pregeneration

### U7. Store extraction output as the repo digest and expose token-budgeted query
- **Goal**: Make graphify's own extraction output the "what is this project" digest agents consult, replacing raw file reads/full vector search for cheap questions.
- **Requirements**: R5, R8
- **Dependencies**: U5, U6
- **Files**:
  - `backend/SupportForge.Ingestion/Code/GitRepoSyncService.cs` — confirm `graphify-out/` location convention (settled in U4)
  - New query-facing service, e.g. `backend/SupportForge.Agents/Tools/GraphifyQueryTool.cs`, parallel to `CodeSearchTool`/`KbSearchTool`
  - `backend/SupportForge.Agents/CodeAnalyzerAgent.cs` — consume the new query tool
- **Approach**:
  1. Because U5's code-path extraction runs with `--no-cluster` (kept LLM-free), run community-labelling as its own explicit, budgeted step here — either a follow-up `graphify extract <path>` without `--no-cluster`, or `graphify label`/`cluster-only` against the existing graph — so `graphify-out/GRAPH_REPORT.md` and community labels exist as the digest without silently making every code ingestion call LLM-backed.
  2. Add a `GraphifyQueryTool` wrapping `graphify query "<question>" --budget N` (default 2000 tokens) via `GraphifyCliRunner`, for agents to call instead of raw grep or full vector search.
  3. Track and log the token cost of this clustering/labelling step as a measured number, not an assumed zero — this feeds U13's cost accounting.
- **Patterns to follow**: `backend/SupportForge.Agents/Tools/CodeSearchTool.cs`/`KbSearchTool.cs` for the existing tool-wrapping-a-service shape.
- **Test scenarios**:
  - Happy path: `graphify query "what is this project" --budget 2000` against a freshly-cloned test repo returns an answer within the token budget.
  - Happy path: `GRAPH_REPORT.md` and community labels exist after extraction.
  - Integration: `CodeAnalyzerAgent` successfully retrieves a graph-query answer through the new tool instead of a raw vector search call.
  - Measurement: community-labelling token cost for a test extraction is captured and logged as a non-placeholder number.
- **Verification**: Acceptance Example — clone a test repo, confirm `GRAPH_REPORT.md` and community labels exist, `graphify query "what is this project" --budget 2000` answers within budget, and the labelling token cost is recorded.

### Phase 3 — Pluggable refresh

### U8. Add per-source refresh modes with two refresh classes
- **Goal**: Implement webhook/polling/manual refresh (R2), correctly split by cost (KTD5) rather than assuming one command covers everything.
- **Requirements**: R2 (KTD5)
- **Dependencies**: U5, U6, U7
- **Files**:
  - `backend/SupportForge.Core/Entities/KbSourceConfig.cs`, `GitHubRepoConfig.cs` — add `RefreshMode` (`Webhook | Polling | Manual`)
  - New webhook endpoints, e.g. `backend/SupportForge.Api/Controllers/WebhooksController.cs` (`/webhooks/github`, `/webhooks/confluence`), signature-validated
  - `backend/SupportForge.Ingestion/IngestionBackgroundService.cs` — extend for polling-mode sources, dispatching by refresh class
  - New manual endpoint, e.g. `backend/SupportForge.Api/Controllers/ResyncController.cs` (`POST /resync/{sourceId}`)
  - Optional: wiring for `graphify hook install` on self-hosted deployments
- **Approach**:
  1. Code refresh (any trigger) → `graphify update <path>` via `GraphifyCliRunner`: cheap, no LLM, safe to run on every push.
  2. Docs/Confluence/website refresh → `graphify check-update <path>` first to check whether a semantic re-extraction is pending; only run the LLM-backed `graphify extract`/`add` when it reports pending, and only on that source's own budgeted schedule — never as a direct reaction to a code webhook.
  3. Webhook handlers validate signature and identify the changed repo/page before enqueueing any refresh. GitHub uses its standard HMAC signature header; Confluence has no equivalent default, so `/webhooks/confluence` requires a configured shared-secret header (validated the same way as GitHub's HMAC check) — an unsigned or unverifiable Confluence webhook is rejected, not accepted on trust.
  4. `IngestionBackgroundService` polls content hash/last-modified for `Polling`-mode sources on an interval, then dispatches by class using the same logic as the webhook path.
  5. `POST /resync/{sourceId}` requires the same admin-level authorization as project/source configuration endpoints (not open to the same audience as end-user ticket submission) — forcing a semantic rebuild is a cost-incurring operation and an unauthenticated version of this endpoint is a budget-exhaustion vector.
- **Patterns to follow**: `backend/SupportForge.Ingestion/IngestionBackgroundService.cs` as the existing background-processing-plus-logging template.
- **Test scenarios**:
  - Happy path: a code-only commit event on a webhook-configured repo triggers `graphify update` and makes zero LLM calls; node/edge counts for untouched files stay stable.
  - Happy path: a doc source change is detected, `check-update` reports pending, and the semantic `extract` path runs on its own schedule, not immediately.
  - Happy path: a polling-mode source with a changed last-modified timestamp triggers the correct refresh class within one poll interval.
  - Happy path: `POST /resync/{sourceId}` forces a semantic rebuild on demand.
  - Error path: an invalid GitHub webhook signature is rejected without triggering any refresh.
  - Error path: a Confluence webhook with a missing or incorrect shared-secret header is rejected without triggering any refresh, mirroring the GitHub case.
  - Error path: an unauthenticated or non-admin call to `POST /resync/{sourceId}` is rejected without triggering any refresh.
  - Edge case: a webhook fires for a repo/page not found in any project's config — logged and ignored, not an unhandled exception.
- **Verification**: Acceptance Example — push a code commit to a webhook-configured repo; confirm `graphify update` re-extracts only changed files and makes no LLM call. Change a doc source and confirm `check-update` flags pending semantic re-extraction and the expensive path runs under its own budget, not on the code webhook.

### Phase 4 — Multi-repo / cross-dependency RCA

### U9. Multi-repo onboarding and per-repo credential resolution
- **Goal**: Lift the one-repo-per-project onboarding limit and replace the single global GitHub token with per-repo credential resolution (R7) — both named as Phase 4 prerequisites since ingestion itself already supports multiple repos.
- **Requirements**: R7
- **Dependencies**: none additional beyond existing entities (can run parallel to U5-U8)
- **Files**:
  - `backend/SupportForge.Ingestion/Code/GitRepoSyncService.cs` — replace single `GitHub:Token` lookup with per-repo credential resolution keyed on `GitHubRepoConfig.AccessTokenSecretName`
  - Admin onboarding flow (project creation/edit UI or API) — support adding multiple repos, not just one
  - `docs/deployment/configuration-guide.md` — document multi-repo onboarding and per-repo credential setup
- **Approach**:
  1. Wire `GitHubRepoConfig.AccessTokenSecretName` to the `ISecretResolver` mechanism named in KTD7 (e.g. Key Vault/Secrets Manager in cloud deployments, environment variables or `dotnet user-secrets` for local/dev) — no ad hoc secret-store decision here, since KTD7 settles it once for both GitHub tokens and the Confluence credential (U6); fall back to the existing global token only when a repo has no per-repo secret configured, so this is additive.
  2. Update the onboarding flow/API to accept a list of repos per project instead of one.
- **Patterns to follow**: existing `GitRepoSyncService` credential-provider wiring for the current global-token path.
- **Test scenarios**:
  - Happy path: two repos in different GitHub orgs, each with a distinct `AccessTokenSecretName`, both clone successfully using their own credentials.
  - Regression: a repo with no `AccessTokenSecretName` configured still clones using the existing global token (backward compatible).
  - Happy path: the onboarding flow accepts and persists two or more repos for one project.
  - Error path: an invalid/missing secret for a configured `AccessTokenSecretName` produces a clear per-repo error rather than a generic auth failure.
- **Verification**: Acceptance Example (partial) — two private repos in different orgs both clone with per-repo credentials.

### U10. Cross-repo RCA: global graph, entry-point resolution, and blast-radius traversal
- **Goal**: Build the actual cross-repo RCA capability (R3) using graphify's own multi-repo graph mechanics, entry-point resolution from ticket text, and the newly-found `affected` command for blast-radius analysis.
- **Requirements**: R3, R5
- **Dependencies**: U5 (code extraction), U9 (multi-repo onboarding/auth)
- **Files**:
  - New global-graph maintenance logic, e.g. `backend/SupportForge.Ingestion/Graphify/GraphifyGlobalGraphService.cs` — wraps `graphify global add/list/remove` and `diagnose multigraph`
  - `backend/SupportForge.Agents/TriageAgent.cs` — add a second, separate service/repo resolution classification alongside the existing intent labels, with the same "unresolved" fallback discipline as `unclear`
  - `backend/SupportForge.Agents/CodeAnalyzerAgent.cs` — add `graphify path`/`query --dfs`/`explain`/`affected` calls (via `GraphifyQueryTool` from U7, extended) with explicit `--graph` targeting the global/merged graph
  - Guardrail integration per `docs/plans/2026-07-29-001-feat-rca-answers-no-code-exposure-plan.md`, applied per repo/service using graphify's `source_location`
- **Approach**:
  1. Maintain a persistent multi-repo graph via `graphify global add <graph.json> --as <tag>` as each configured repo is extracted/updated; run `graphify diagnose multigraph` as a gate before trusting any merged/global graph for RCA. When the gate fails (same-endpoint edge collapse detected), RCA degrades to independent per-repo `graphify query` calls against each repo's own graph and logs a warning identifying the failing merge — it does not silently serve from the untrusted merged graph.
  2. Add service/repo resolution to `TriageAgent` as a second classification independent of `kb_question | code_issue | code_question | screenshot_error | unclear`: explicit metadata when the source system provides it, else ticket text matched against configured repo/service names, degrading to "unresolved" (whole-graph query) on ambiguity — mirroring the existing `unclear` fallback pattern.
  3. From the resolved entry point: `graphify path "<entryPoint>" "<suspect>"` for a hypothesis-driven shortest path; `graphify query "<question>" --dfs --graph <path>` to follow one thread deep when there isn't; `graphify affected "<node>" --relation R --depth N --graph <path>` for reverse traversal / blast-radius ("what else does this break?").
  4. Community detection surfaces repo clusters without configured dependency links as a suggestion signal only — never auto-added to `Project.Repos` (preserves R1's manual-config decision).
  5. Apply no-code-exposure guardrails per repo/service using `source_location` from graphify's citations as the key.
- **Patterns to follow**: `backend/SupportForge.Agents/TriageAgent.cs`'s existing intent-classification-with-`unclear`-fallback pattern, applied to the new service-resolution classification.
- **Test scenarios**:
  - Happy path: a global/merged graph from two peer repos with a real cross-repo call passes `diagnose multigraph` cleanly.
  - Happy path: `graphify path`/`query --dfs` with explicit `--graph` finds a real cross-repo dependency path and cites the correct repo via `source_location`.
  - Happy path: `graphify affected` returns the correct blast radius for a known node with cross-repo callers.
  - Edge case: an ambiguous ticket-to-service match degrades to a whole-graph query instead of confidently starting from the wrong service.
  - Error path: a merged/global graph that fails `diagnose multigraph` causes RCA to fall back to per-repo queries with a logged warning, not a silent answer from the untrusted merge.
  - Integration: the no-code-exposure guardrail is applied correctly per repo/service on a multi-repo answer.
  - Regression: a query call that omits `--graph` is caught in review/tests as a defect (per Appendix finding — forgetting `--graph` silently queries a single repo).
- **Verification**: Acceptance Example — build a global/merged graph from two peer repos with a real cross-repo call; confirm `diagnose multigraph` is clean, `path`/`query --dfs` with explicit `--graph` finds the cross-repo path, `affected` returns the correct blast radius, the answer cites the right repo, guardrails apply per repo, and an ambiguous service match degrades to whole-graph query.

### Phase 5 — Context rot, prompt caching, logging, observability, cost

### U11. Logging and OpenTelemetry observability
- **Goal**: Add structured logging and real tracing/metrics (R8) — currently `ILogger` appears in one file and no OpenTelemetry package is referenced anywhere.
- **Requirements**: R8
- **Dependencies**: none additional (can run parallel to U5-U10 once U1's capability seam exists for token counts)
- **Files**:
  - Every `IAgent` implementation (`backend/SupportForge.Agents/TriageAgent.cs`, `KbResearcherAgent.cs`, `CodeAnalyzerAgent.cs`, `VisionAnalyzerAgent.cs`, `DrafterAgent.cs`) — inject `ILogger<T>`
  - `backend/SupportForge.Agents/CoordinatorPipeline.cs` — tracing spans per agent step
  - `backend/SupportForge.Api/Program.cs` — register `OpenTelemetry.Extensions.Hosting` + exporters (console for dev, OTLP for prod)
  - New `PackageReference`s for OpenTelemetry SDK/exporters (none exist today per Appendix finding #2)
- **Approach**:
  1. Add the OpenTelemetry SDK, hosting integration, and exporter packages — this is new infrastructure, not activation of something already present.
  2. Inject `ILogger<T>` into every agent, logging intent classification, graph-query hits, token counts (from U1/U2's `LastTotalTokens`), and per-step latency.
  3. Add a tracing span per agent step in `CoordinatorPipeline`, following the existing swappable-provider config pattern from `docs/deployment/configuration-guide.md` (console exporter for dev, OTLP for prod).
  4. Apply a redaction rule at the logging/tracing boundary: known secret-shaped values (API keys, tokens, provider base URLs with embedded credentials) are scrubbed before they reach a log line, span attribute, or exporter — including U4's captured subprocess stderr, which can surface provider environment values from U3. Raw ticket/KB text is logged at debug level only, not at the info level used for routine operational logs, since it may carry customer PII.
- **Patterns to follow**: `backend/SupportForge.Ingestion/IngestionBackgroundService.cs` as the one existing `ILogger` usage to mirror.
- **Test scenarios**:
  - Happy path: a single end-to-end query produces a log line and a span for each agent step.
  - Happy path: token/latency data is present and non-placeholder in the exported span data.
  - Integration: switching the exporter from console to OTLP via config requires no code change.
  - Edge case: an agent failure still produces a completed (error-status) span rather than an orphaned trace.
- **Verification**: Acceptance Example (partial) — one end-to-end query produces a log line and a span per agent with token/latency data exported through a real OTLP or console exporter.

### U12. Context-rot mitigation and prompt caching
- **Goal**: Replace raw file dumps/broad vector search with token-budgeted graph queries, and structure prompts for provider prompt-caching (R8).
- **Requirements**: R8
- **Dependencies**: U7 (query tool), U1/U2 (per-provider caching surfaces)
- **Files**:
  - `backend/SupportForge.Agents/CodeAnalyzerAgent.cs`, `KbResearcherAgent.cs` — prefer `graphify query --budget N`/`explain` over raw file reads or full vector search; escalate to full reads only when the graph answer reports itself insufficient
  - Prompt-construction code in each agent — restructure so static content (system prompt, `GRAPH_REPORT.md` digest) forms a stable prefix
- **Approach**:
  1. Make graph-query/`explain` the default retrieval path; full file reads become an explicit escalation, not the default.
  2. Keep tool-facing output terse (compact facts + citations) so the Phase 1-2 token savings aren't given back at the formatting layer.
  3. Structure prompts with a stable static prefix (system prompt + digest) and use each provider's native prompt-caching mechanism (Anthropic's native API is more capable here than the OpenAI-compatible surface, which is part of why KTD1's split matters).
- **Patterns to follow**: `backend/SupportForge.Agents/Tools/CodeSearchTool.cs`/`KbSearchTool.cs` as the existing retrieval-tool shape being extended.
- **Test scenarios**:
  - Happy path: a query answerable from the graph digest does not trigger a full file read.
  - Edge case: a query that the graph reports as insufficient correctly escalates to a full file read.
  - Happy path: a repeated query with an unchanged digest produces a measurable prompt-cache hit on a provider that supports it.
- **Verification**: Acceptance Example (partial) — a repeated query shows a prompt-cache hit.

### U13. Cost accounting and feedback loop
- **Goal**: Track graphify's own build cost alongside query cost (R8), and route existing user feedback into graph-quality signal via `save-result`/`reflect`.
- **Requirements**: R8
- **Dependencies**: U7, U11
- **Files**:
  - Cost-tracking integration reading `graphify-out/cost.json` (written automatically by graphify) into the observability pipeline from U11
  - Existing Mark Useful / Escalate feedback flow and `FeedbackEntry` — wire to `graphify save-result` (`useful | dead_end | corrected`)
  - New scheduled or on-demand `graphify reflect` invocation to aggregate feedback into a lessons document
- **Approach**:
  1. Read `graphify-out/cost.json` after each extraction/update/query as an observability input, reported as a recurring number rather than a one-off.
  2. Wire the existing Mark Useful/Escalate UI action to call `graphify save-result` with the corresponding outcome.
  3. Periodically (or on-demand) run `graphify reflect` and surface its aggregated output somewhere reviewable (log, admin view, or file).
  4. Track `graphify benchmark` as the metric that validates the plan's core token-reduction premise — make it a recurring reported number.
- **Patterns to follow**: existing `FeedbackEntry`/Mark Useful flow (locate via the feedback repository referenced in `Program.cs`).
- **Test scenarios**:
  - Happy path: a Mark Useful click results in a `graphify save-result` call with `useful`.
  - Happy path: `graphify reflect` output is retrievable after at least one recorded result.
  - Measurement: `graphify benchmark` produces a token-reduction figure for a test corpus.
  - Integration: `cost.json` values are visible in the same observability pipeline as U11's spans/logs.
- **Verification**: Acceptance Example (partial) — `graphify benchmark` reports a token-reduction figure; a Mark Useful click lands in `graphify-out/memory/` and appears in `graphify reflect` output.

---

## Verification Contract

- **Phase 0 (U1-U3)**: with chat and embeddings pointed at *different* providers, one query completes end to end; `LastTotalTokens` is non-zero for each call; switching either provider requires only config; a vision-incapable chat model causes `VisionAnalyzerAgent` to be skipped with a clear reason, not an exception; startup with an incompatible graphify backend configuration (Bedrock/Azure with no gateway) fails loudly at configuration time.
- **Phase 1 (U4-U6)**: `graphify extract` over a test repo plus a Confluence-derived markdown file, and `graphify add <url>` for a website, produce `graphify-out/graph.json` with nodes from all three and correct `source_location`; a code-only extraction completes with zero LLM calls.
- **Phase 2 (U7)**: cloning a test repo produces `graphify-out/GRAPH_REPORT.md` and community labels; `graphify query "what is this project" --budget 2000` answers within budget; community-labelling token cost is recorded as a measured number.
- **Phase 3 (U8)**: a code commit to a webhook-configured repo triggers `graphify update`, re-extracting only changed files (stable node/edge counts for untouched files) with no LLM call; a doc-source change is flagged pending by `check-update` and its expensive re-extraction runs under its own budget, not on the code webhook; an invalid GitHub signature, a missing/incorrect Confluence shared secret, and an unauthenticated `/resync/{sourceId}` call are all rejected without triggering any refresh.
- **Phase 4 (U9-U10)**: a global/merged graph from two peer repos passes `diagnose multigraph`; `path`/`query --dfs` with explicit `--graph` finds a real cross-repo path; `affected` returns the correct blast radius; the answer cites the correct repo; guardrails apply per repo; an ambiguous service match degrades to whole-graph query; two private repos in different orgs both clone with per-repo credentials.
- **Phase 5 (U11-U13)**: one end-to-end query produces a log line and a span per agent with token/latency data exported through a real OTLP or console exporter; a repeated query shows a prompt-cache hit; `graphify benchmark` reports a token-reduction figure; a Mark Useful click lands in `graphify-out/memory/` and appears in `graphify reflect` output.

## Definition of Done

- All 13 implementation units (U1-U13) are implemented and their unit-level Verification criteria pass.
- The Verification Contract's per-phase acceptance checks all pass against a real (or realistically test-doubled) `graphify` CLI installation and at least two configured LLM providers.
- `docs/deployment/configuration-guide.md` documents every new `Llm:*`/`Embeddings:*` provider value, the graphify backend-derivation behavior and the Bedrock/Azure gateway requirement, new KB source types and refresh modes, per-repo credential setup, multi-repo onboarding, and the Python/graphify host runtime prerequisite — reconciled with its existing uncommitted local modifications first.
- No regression in existing agent pipeline tests (`backend/SupportForge.Api.Tests/Agents/*`, `backend/SupportForge.Api.Tests/Ingestion/*`).
- Logging and tracing are visible end-to-end for at least one real query, and cost accounting (LLM tokens + graphify build cost) is reported as real numbers, not placeholders.

---

### Critical Files
- `backend/SupportForge.Agents/ILlmClient.cs`, `OpenAiLlmClient.cs` — split by capability (U1); add Anthropic and Bedrock implementations, wire Azure via the `Microsoft.Extensions.AI` Azure connector (U2)
- `backend/SupportForge.Api/Program.cs` — where `Llm:Provider` selects the config section today; extends to per-capability provider flags and graphify backend derivation (U1, U3)
- `backend/SupportForge.Api/appsettings.json` — ships `Llm:Provider = NvidiaNim` with `OpenAI`/`NvidiaNim` sections; new providers and graphify backend config land here (U2, U3)
- `backend/SupportForge.Core/Entities/KbSourceConfig.cs` — extend `KbSourceType`, add `RefreshMode` and explicit repo/path association (U6, U8)
- `backend/SupportForge.Core/Entities/GitHubRepoConfig.cs` — add `RefreshMode`; per-repo credential resolution replaces the unread `AccessTokenSecretName` (U8, U9)
- `backend/SupportForge.Ingestion/Code/GitRepoSyncService.cs` — hook `graphify extract`/`update` after clone/pull (U5); per-repo credential resolution (U9)
- `backend/SupportForge.Ingestion/Code/CodeIngestionJobFactory.cs`, `CodeIngestionJob.cs` — already fans out per repo; graphify invocation slots in here (U5)
- `backend/SupportForge.Ingestion/Documents/DocumentIngestionJobFactory.cs`, `DocumentIngestionJob.cs` — remove `Repos.FirstOrDefault()`; graphify extraction; Confluence-to-markdown fetch (U6)
- `backend/SupportForge.Ingestion/IngestionBackgroundService.cs` — extend for polling refresh dispatched by class (U8); logging template (U11)
- `backend/SupportForge.Agents/Tools/CodeSearchTool.cs`, `KbSearchTool.cs` — depend on the split embeddings seam (U1); joined by a new graphify query tool (U7)
- `backend/SupportForge.Agents/CodeAnalyzerAgent.cs`, `TriageAgent.cs` — graphify query/path/explain/affected calls, service resolution alongside existing intent labels, repo-aware citations and guardrails (U10, U12)
- `backend/SupportForge.Agents/CoordinatorPipeline.cs` — tracing spans, structured logging, vision-capability gating (U1, U11)
- `docs/deployment/configuration-guide.md` — document new `Llm:*` values, graphify backend environment, new KB source types, refresh modes, per-repo auth, multi-repo onboarding, Python/graphify runtime requirement. **Note**: this file has uncommitted local modifications documenting `Llm:*`; reconcile before editing (U2, U3, U9)

### Outstanding Questions

**Resolved during this planning pass:**
- **D1 — seam granularity.** Resolved via KTD1: split `ILlmClient` into capability-scoped interfaces (chat, embeddings, vision-capable chat).
- **D2 — graphify backend for Bedrock/Azure deployments.** Resolved via KTD2: require an OpenAI-compatible gateway (e.g. LiteLLM) in front of Bedrock/Azure and point `--backend openai` at it.

**Deferred to implementation** (see Planning Contract § Deferred to Implementation): exact Bedrock connector library, per-repo guardrail policy schema, entry-point ambiguity-scoring threshold, and in-process-vs-worker/container placement for graphify.

### Appendix: Verification Findings (2026-08-01)
What the verification pass changed, and why. Numbered for traceability.

1. **Broken doc citations.** The plan cited `configuration-guide.md:7-20` for `Llm:Provider` and `:31` for the private-repo gap. At `bfcbad0` those lines are the old `OpenAI:ApiKey`/`VectorStore` table and a dark-mode deviation note. The `Llm:*` documentation exists only as an **uncommitted modification** in the main checkout. All line-number citations were replaced with section/file references, and the uncommitted state is now flagged.
2. **OpenTelemetry was never present.** "Activate the already-present package" was wrong: no `PackageReference` to any OpenTelemetry package exists. Only `OpenTelemetry.Api` appears transitively via `Microsoft.Extensions.AI`. Phase 5 (U11) now specifies adding the SDK and exporters.
3. **Skill syntax vs CLI syntax.** Every command was written in `/graphify` skill form (`graphify <path> --update`). The installed CLI is subcommand-based (`graphify update <path>`). Since the backend spawns a process, all commands were rewritten in CLI form.
4. **`--wiki` and `--mcp` do not exist in the CLI** (`graphify 0.9.9`). Phase 2's repo card and the "expose graphify via MCP" note both assumed skill-only surfaces. Replaced with the real outputs of `extract` plus `tree` / `export callflow-html`.
5. **`update` is code-only.** CLI help is explicit: "re-extract **code** files and update the graph (no LLM needed)." The claim that all refresh converges on one cheap command was false for docs, Confluence, and websites. Phase 3 (U8) now has two refresh classes and uses `check-update` as the gate for the expensive one.
6. **graphify has its own provider surface.** `extract --backend` accepts `gemini|kimi|claude|openai|deepseek|ollama` via environment variables, independent of `Llm:*` — and **excludes Bedrock and Azure**. Raised to Phase 0c (U3) and resolved via KTD2.
7. **Cross-repo merge mechanics were unstated.** Added `global add/list/remove` as the preferred persistent mechanism (KTD4), the explicit `--graph` requirement for querying merged graphs, and `diagnose multigraph` as a correctness gate against same-endpoint edge collapse (U10).
8. **`ILlmClient` bundles chat and embeddings.** Anthropic has no embeddings API, so `Llm:Provider = Anthropic` breaks ingestion outright; `EmbedAsync` is additionally bound to the raw OpenAI wire format by a documented NIM workaround. Vision has the same problem in a weaker form. Resolved via KTD1 (U1) — the largest substantive change in this revision.
9. **Private-repo auth was described imprecisely.** `GitRepoSyncService` already authenticates with one global `GitHub:Token`; the per-repo `AccessTokenSecretName` is modeled but never read. The requirement is per-repo credential resolution for cross-org repos (U9), not "wire the secret store."
10. **"Multi-repo modeled but under-used" was wrong.** `CodeIngestionJobFactory` fans out per repo and `FreshnessCalculator` tracks per-repo staleness. The actual blockers are the one-repo onboarding flow and `DocumentIngestionJobFactory`'s `Repos.FirstOrDefault()` assumption — both now named as Phase 1/4 prerequisites (U6, U9).
11. **Phase 0 verification was untestable.** "Confirm equivalent answers from two providers" is not checkable against non-deterministic models. Replaced with contract assertions (mixed-provider run, non-zero token counts, config-only switching, loud failure on incompatible config) in the Verification Contract.
12. **No runtime story for a Python dependency.** The repository contains no Dockerfile or container definition. Running graphify requires Python plus the `graphifyy` package on the backend host, and the .NET side needs process-spawn plumbing. Resolved via KTD6 and U4; in-process-vs-container placement remains Deferred to Implementation.

Additional capabilities found during verification and folded into the plan as opportunities rather than gaps: `affected` (reverse traversal / blast radius, U10), `save-result` + `reflect` (feedback loop onto the existing Mark Useful flow, U13), `hook install` (git-hook refresh trigger, U8), and `benchmark` (token-reduction measurement, U13).
