---
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
---

# KB/RAG Infrastructure for Multi-Repo, Multi-Provider Agent Context - Plan

## Goal Capsule
- **Objective**: design, as one integrated system, cross-dependency repo coverage for RCA, clone-time KB pregeneration, pluggable external KB sources (Confluence/PDF/MD/TXT/websites/GitHub repos), source refresh, and context-rot/caching/logging/observability for the Support-pipeline agent framework — sequenced by dependency order, not urgency (no single piece is on fire today).
- **Product authority**: this brainstorm session; no separate STRATEGY.md consulted.
- **Open blockers**: none — all clarifying questions were resolved during the session (see Key Decisions).

## Product Contract

### Context
Support-pipeline's agent pipeline (`Triage -> KbResearcher -> CodeAnalyzer -> VisionAnalyzer -> Drafter`, see `docs/agentic-pipeline.md`) currently ingests code from a single vector store per project, sourced from `Project.Repos` (already `List<GitHubRepoConfig>`, so multi-repo-per-project is modeled but under-used), plus local documents/PDFs via `KbSourceConfig` (Confluence is a stub, no web/GitHub-doc connectors exist). There's no refresh mechanism, no clone-time summarization, and almost no logging/observability (`ILogger` used in exactly one file: `backend/SupportForge.Ingestion/IngestionBackgroundService.cs`; OpenTelemetry is an unused transitive dependency).

### Key Decisions
- Cross-dependency repos are added via **manual config** (extend `Project.Repos`), not auto-detected from manifests — v1 keeps it explicit and predictable.
- Refresh must support **all three trigger modes** (webhook, scheduled polling, manual), configurable **per source**, so the admin picks what fits each connector.
- **Microservices break "Primary vs Dependency."** A binary role doesn't fit a peer-service architecture where no repo is inherently primary. Phase 4 is designed around graphify's own dependency graph, not a hand-built role field.
- **Model/provider flexibility is a core project goal**, not an afterthought — must support OpenAI, Anthropic, AWS Bedrock, Azure, NVIDIA NIM, and custom-hosted models (Phase 0).
- **Use the `graphify` skill as the KB engine**, not a bespoke chunk-and-embed pipeline. Graphify turns any folder (code, docs, PDFs, images, video) into a queryable knowledge graph: AST-based extraction for code needs **no LLM call at all** (big token savings vs. embedding every file), it natively merges multiple repos into one cross-repo graph with community detection, it re-extracts only changed files on `--update`, and it exposes `query`/`path`/`explain` for token-budgeted, targeted retrieval instead of raw chunk dumps. The vector store is demoted from "the KB" to an optional secondary index for prose-heavy semantic search where graph traversal isn't the right tool.

### Recommended Approach — 6 phases, in dependency order

#### Phase 0 (foundation, parallel to Phase 1): Harden provider flexibility
The seam already exists and is in good shape — extend it rather than replace it:
- `ILlmClient` (`backend/SupportForge.Agents/ILlmClient.cs`) is already provider-agnostic (`CompleteAsync`/`StreamCompleteAsync`/`EmbedAsync`/`AnalyzeImageAsync`), and the only implementation, `OpenAiLlmClient`, is itself built on `Microsoft.Extensions.AI`'s `IChatClient` abstraction (`backend/SupportForge.Agents/OpenAiLlmClient.cs:3,10`) — not hardcoded to the OpenAI wire format. This is why NVIDIA NIM already works today (config-guide.md:7-18): NIM is OpenAI-compatible, so it's just a `BaseUrl` swap under the same client.
- **True custom-hosted / OpenAI-compatible servers** (vLLM, TGI, LM Studio, etc.) already work today the same way NIM does — no new code, just a new `Llm:<Provider>` config section with the right `BaseUrl`.
- **Azure OpenAI**: `Microsoft.Extensions.AI` ships an official Azure `IChatClient`/`IEmbeddingGenerator` connector — wire it in as a new provider branch (deployment-based URL + `api-key` auth differ from plain OpenAI).
- **AWS Bedrock**: different wire format and SigV4 auth — needs its own `ILlmClient` implementation (or an `IChatClient` adapter if a community/official Bedrock connector for `Microsoft.Extensions.AI` fits), registered as `Llm:Provider = Bedrock`.
- **Anthropic (native API)**: not OpenAI-compatible — needs its own `ILlmClient` implementation wrapping Anthropic's SDK/HTTP API (message format, system-prompt-as-top-level-field, streaming events all differ). Also relevant to Phase 5's prompt caching, since Anthropic's native prompt-caching API is more capable than the OpenAI-compatible surface exposes.
- All providers keep selecting via `Llm:Provider` config exactly as documented in `docs/deployment/configuration-guide.md:7-20` — no code change to switch. `LastTotalTokens` must be populated consistently across implementations since Phase 5's logging/observability depends on it.

#### Phase 1 (foundation): Unify KB sources through graphify, not bespoke connectors
Everything else builds on this. Instead of an `IKbConnector` per source type, use graphify's own coverage and add a thin fetch step only where content isn't already a local file:
- **Code repos**: `graphify <clonedPath>` after `GitRepoSyncService.CloneOrPull` — AST extraction needs no LLM/API key, so this is near-free per repo.
- **Local documents/PDF/MD/TXT** (today's `Documents` type): unchanged input, but ingest via `graphify <folder>` instead of `DocumentChunker` + embeddings — graphify already detects and handles docs/papers/images/video in the same corpus.
- **Websites and arbitrary URLs**: `graphify add <url>` fetches and folds the page into the existing graph — covers ad hoc web KB sources without a bespoke crawler.
- **Confluence**: no native connector in graphify, so keep a small fetch step (Confluence REST API -> page exported as markdown into the corpus folder), then let graphify ingest it like any other doc — this is the one place a thin custom connector is still needed.
- **Multiple GitHub repos, including cross-dependency ones**: `graphify <url1> <url2> ...` merges them into a single cross-repo graph directly — this is the natural home for Phase 4's dependency coverage, not a separate mechanism.
- Keep `IVectorStoreService`/Chroma/Pinecone as a secondary index only where prose-similarity search beats graph traversal (e.g. "find KB articles similar in wording to this ticket") — it stops being the primary KB path.

#### Phase 2: Clone-time KB pregeneration — graphify's build IS the repo card
Directly cuts the token cost problem in Phase 5, not just clone-time cost:
- Run `graphify <clonedPath> --wiki` right after `GitRepoSyncService.CloneOrPull` (`backend/SupportForge.Ingestion/Code/GitRepoSyncService.cs:18-31`). `GRAPH_REPORT.md`, the community labels, and the god-node list already are the "what is this project" digest — no separate summarization step to design or maintain.
- Store `graphify-out/` alongside the cloned repo; point agents at `graphify query "<question>" --budget N` for cheap, token-capped answers instead of raw grep or a full vector search.
- Regenerate via `graphify <clonedPath> --update` when Phase 3's refresh detects a change — `--update` re-extracts only new/changed files, so this is cheap on every refresh, not just the first clone.

#### Phase 3: Pluggable refresh (webhook / polling / manual, per source) — all converge on `graphify --update`
- Add a `RefreshMode` field (`Webhook | Polling | Manual`) to `KbSourceConfig` and `GitHubRepoConfig`, selectable per source.
- **Webhook**: new `/webhooks/github` and `/webhooks/confluence` endpoints, signature-validated, enqueue `graphify <path> --update` for just the changed repo/page (Confluence pages re-fetched to markdown first, same as initial ingestion).
- **Polling**: extend `IngestionBackgroundService` (the one file that already owns background ingestion + logging) to periodically check content hash / last-modified for sources in `Polling` mode, then run `--update`.
- **Manual**: a simple `POST /resync/{sourceId}` endpoint that runs `--update` on demand.
- graphify's own manifest-based change detection (used internally by `--update`) replaces a hand-rolled content-hash diff — one less thing to build.

#### Phase 4: Multi-repo / cross-dependency RCA — graphify's cross-repo graph, not a hand-built one
A flat `Primary`/`Dependency` role doesn't fit a microservice project, where many repos are peers and "primary" depends entirely on which service the current ticket is actually about. Graphify already builds exactly this kind of graph — don't reimplement it:
- Configure related repos on `Project.Repos` (manual config, per the locked decision) and build them as one merged graph: `graphify <repo1-path> <repo2-path> ... --directed` — `--directed` preserves source->target call direction, which matters for tracing root cause across service boundaries.
- **Entry-point resolution per ticket**: Triage (`TriageAgent.cs`) classifies which service/repo a ticket is *about* (explicit metadata if the source system provides it, else signal in ticket text matched against repo/service names) — this becomes the starting node for graph traversal, not a fixed project-level "primary."
- **Cross-repo RCA query**: from the entry-point node, use `graphify path "<entryPointService>" "<suspectService>"` to trace the shortest dependency path when the user/agent has a hypothesis, or `graphify query "<ticket question>" --dfs` to trace a specific root-cause path across repo boundaries when they don't — DFS mode is built for exactly this "follow one thread deep" case, vs. BFS's broad-context default.
- Community detection surfaces which repos cluster together even when a dependency link isn't explicitly configured — useful as a suggestion signal for repos the admin forgot to add to `Project.Repos`, without auto-adding them (keeps the locked manual-config decision).
- Resolve the flagged MVP gap: private-repo auth token wiring (`docs/deployment/configuration-guide.md:31`) — required before dependency repos (often private) can actually be cloned for graphify to ingest.
- Apply the no-code-exposure guardrails from `docs/plans/2026-07-29-001-feat-rca-answers-no-code-exposure-plan.md` **per repo/service** when formatting the final answer — graphify's `source_location` citations make it easy to tell which repo a fact came from, so the guardrail filter has a clean signal to key off.

#### Phase 5 (cross-cutting): Context rot, prompt caching, logging, observability
- **Context rot**: replace raw file/document dumps and broad vector search with `graphify query --budget N` (token-capped by construction) or `graphify explain "<node>"` for a single-concept lookup; agents only escalate to full file reads when the graph answer says it's insufficient. Keep tool-facing output terse — graph query results and `explain` responses should stay compact facts-plus-citations, not prose padding, so the token savings from Phase 1-2 aren't given back at the formatting layer.
- **Prompt caching**: structure agent prompts so static content (system prompt, the repo's `GRAPH_REPORT.md` digest) forms a stable prefix, and use provider prompt-caching (Anthropic/OpenAI) for it — this pairs naturally with Phase 2 since the digest changes only on `--update`, not per query.
- **Logging**: inject `ILogger<T>` into every `IAgent` implementation (today only `IngestionBackgroundService` has one) — log intent classification, graph-query hits, token counts, and latency per pipeline step in `CoordinatorPipeline.cs`.
- **Observability**: activate the already-present (but unused) OpenTelemetry package — add tracing spans per agent in the `Workflow` pipeline, export token/latency/cache-hit metrics, following the existing swappable-provider pattern from `docs/deployment/configuration-guide.md` (console/file exporter for dev, OTLP for prod). Also track graphify's own per-run token cost (`graphify-out/cost.json`, written automatically) as an observability input — KB build cost is as real as query cost.

### Critical Files
- `backend/SupportForge.Agents/ILlmClient.cs`, `OpenAiLlmClient.cs` — provider seam; add `AnthropicLlmClient`, `BedrockLlmClient`, wire Azure via `Microsoft.Extensions.AI`'s Azure connector
- `backend/SupportForge.Core/Entities/KbSourceConfig.cs` — extend `KbSourceType`, add `RefreshMode`
- `backend/SupportForge.Core/Entities/GitHubRepoConfig.cs` — add `RefreshMode` (no hand-rolled `ServiceName`/`DependsOn` needed — graphify's own graph carries the relationships)
- `backend/SupportForge.Ingestion/Code/GitRepoSyncService.cs` — hook `graphify <path> --wiki` after clone, `--update` after subsequent pulls
- `backend/SupportForge.Ingestion/Documents/` — replace bespoke chunk/embed calls with graphify invocation for local docs; keep a thin Confluence-to-markdown fetch step before handing off to graphify
- `backend/SupportForge.Ingestion/IngestionBackgroundService.cs` — extend for polling refresh mode (triggers `--update`), keep as the logging template
- `backend/SupportForge.Agents/CodeAnalyzerAgent.cs`, `TriageAgent.cs` — replace/augment vector search calls with `graphify query`/`path`/`explain` calls (directly, or via `graphify --mcp` as a tool the agent framework calls), repo-aware citations, per-repo guardrails
- `backend/SupportForge.Agents/CoordinatorPipeline.cs` — add tracing spans, structured logging
- `docs/deployment/configuration-guide.md` — document new `Llm:Provider` values, new KB source types, refresh modes, auth requirements, and graphify as the KB engine

### Acceptance Examples (verification, per phase)
- **Phase 0**: run the same query against two configured providers (e.g. OpenAI and Anthropic) and confirm equivalent answers, correct `LastTotalTokens`, and no code change required to switch — only config.
- **Phase 1**: run `graphify` against a test project's repo plus a Confluence-derived markdown file and a `graphify add <url>` website; confirm `graphify-out/graph.json` contains nodes from all three sources with correct citations.
- **Phase 2**: clone a test repo, confirm `graphify-out/GRAPH_REPORT.md` and community labels exist and `graphify query "what is this project"` returns a sensible answer within budget.
- **Phase 3**: push a commit to a webhook-configured repo and confirm `graphify --update` re-extracts only the changed files (graph node/edge counts for untouched files stay stable); separately verify a polling-mode source picks up a manual content change within one poll interval.
- **Phase 4**: build a merged graph from two peer repos with a real cross-repo call, ask an RCA question whose root cause lives in the second repo, confirm `graphify path`/`query --dfs` finds the cross-repo path, the answer cites the correct repo via `source_location`, and guardrails apply per-repo.
- **Phase 5**: inspect logs/traces for a single end-to-end query and confirm each agent step has a log line + span with token/latency data, a repeated query shows a prompt-cache hit, and `graphify-out/cost.json` is being read into the observability pipeline.

### Outstanding Questions
- None blocking. Future refinement candidates for a follow-up `ce-plan` pass: exact per-repo guardrail policy schema, choice of Bedrock connector library, and how the entry-point resolver should score ambiguous ticket-to-service matches.
