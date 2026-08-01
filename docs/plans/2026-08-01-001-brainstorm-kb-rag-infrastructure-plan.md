---
artifact_contract: ce-unified-plan/v1
artifact_readiness: requirements-only
product_contract_source: ce-brainstorm
---

# KB/RAG Infrastructure for Multi-Repo, Multi-Provider Agent Context - Plan

## Goal Capsule
- **Objective**: design, as one integrated system, cross-dependency repo coverage for RCA, clone-time KB pregeneration, pluggable external KB sources (Confluence/PDF/MD/TXT/websites/GitHub repos), source refresh, and context-rot/caching/logging/observability for the Support-pipeline agent framework — sequenced by dependency order, not urgency (no single piece is on fire today).
- **Product authority**: this brainstorm session; no separate STRATEGY.md consulted.
- **Open blockers**: none blocking design. Two decisions must be made before Phase 0/1 implementation starts — see Outstanding Questions (D1: chat/embedding seam split, D2: graphify semantic backend for Bedrock/Azure deployments).

## Verification Pass (2026-08-01)
This plan was re-verified against the repository at commit `bfcbad0` and against the installed graphify CLI (`graphify 0.9.9`). Twelve gaps were found and are resolved inline below; the paragraph-level record is in **Appendix: Verification Findings** so implementers can see what changed and why. Claims that survived verification unchanged are not re-listed there.

## Product Contract

### Context
Support-pipeline's agent pipeline (`Triage -> KbResearcher -> CodeAnalyzer -> VisionAnalyzer -> Drafter`, see `docs/agentic-pipeline.md`) ingests code into a vector store per project, sourced from `Project.Repos`, plus local documents/PDFs via `KbSourceConfig` (Confluence is an unimplemented enum member; no web or GitHub-doc connectors exist). There is no refresh mechanism, no clone-time summarization, and effectively no logging or observability: `ILogger` appears in exactly one file (`backend/SupportForge.Ingestion/IngestionBackgroundService.cs`), and no OpenTelemetry package is referenced by any project.

Multi-repo ingestion is **already wired end to end**, contrary to the pre-verification framing: `CodeIngestionJobFactory.CreateJobs` fans out one `CodeIngestionJob` per entry in `project.Repos`, and `FreshnessCalculator` tracks staleness per repo. The real constraints are upstream and adjacent:
- The Admin onboarding flow and `docs/deployment/configuration-guide.md` describe adding **one** GitHub repo per project.
- `DocumentIngestionJobFactory` resolves KB document folders against `project.Repos.FirstOrDefault()` (an explicit `ponytail:` shortcut) — a first-repo assumption that becomes wrong the moment a project has peer repos.
- `GitRepoSyncService` authenticates every clone with a single global `GitHub:Token`; `GitHubRepoConfig.AccessTokenSecretName` is modeled but never read.

### Key Decisions
- Cross-dependency repos are added via **manual config** (`Project.Repos`), not auto-detected from manifests — v1 keeps it explicit and predictable.
- Refresh must support **all three trigger modes** (webhook, scheduled polling, manual), configurable **per source**, so the admin picks what fits each connector.
- **Microservices break "Primary vs Dependency."** A binary role doesn't fit a peer-service architecture where no repo is inherently primary. Phase 4 is designed around graphify's own dependency graph, not a hand-built role field.
- **Model/provider flexibility is a core project goal**, not an afterthought — must support OpenAI, Anthropic, AWS Bedrock, Azure, NVIDIA NIM, and custom-hosted models (Phase 0). Verification showed this goal has **two** provider surfaces, not one: the app's own `Llm:*` config, and graphify's independent `extract --backend` selection. Both must be satisfied.
- **Use graphify as the KB engine**, not a bespoke chunk-and-embed pipeline. AST-based code extraction needs no LLM call, it re-extracts only changed code on `update`, and `query`/`path`/`explain`/`affected` give token-budgeted, targeted retrieval instead of raw chunk dumps. The vector store is demoted from "the KB" to an optional secondary index for prose-similarity search where graph traversal isn't the right tool.
- **Integrate graphify via its headless CLI, not its agent skill.** The `/graphify` skill orchestrates subagents and is only usable inside an agent host. The .NET backend must shell out to the installed `graphify` executable, whose surface is subcommand-based (`extract`, `update`, `query`, `path`, `explain`, `affected`, `add`, `merge-graphs`, `global`, `check-update`, `benchmark`). Every command in this plan is written in CLI form.

### Recommended Approach — 6 phases, in dependency order

#### Phase 0 (foundation, parallel to Phase 1): Harden provider flexibility across *both* provider surfaces

**0a — Split the app's LLM seam by capability.** The existing seam is genuinely provider-agnostic for chat and is the right thing to build on, but it cannot reach the stated provider set in its current shape:
- `ILlmClient` (`backend/SupportForge.Agents/ILlmClient.cs`) bundles `CompleteAsync`/`StreamCompleteAsync` (chat), `EmbedAsync` (embeddings), and `AnalyzeImageAsync` (vision) behind one interface with one implementation and one `Llm:Provider` flag. **Anthropic ships no embeddings API**, so `Llm:Provider = Anthropic` would leave ingestion with no `EmbedAsync` — the interface makes the stated goal unreachable, not merely inconvenient.
- `OpenAiLlmClient.EmbedAsync` also bypasses the typed `Microsoft.Extensions.AI` layer to POST raw OpenAI-shaped JSON (a deliberate, documented workaround so NIM's `input_type` survives). It is therefore structurally bound to the OpenAI embeddings wire format in a way `CompleteAsync` is not.
- **Requirement**: split chat, embeddings, and vision into independently-configured seams (e.g. `Llm:Provider`, `Embeddings:Provider`, `Vision:Provider`), so a deployment can run Anthropic chat with NIM or Bedrock embeddings. Vision additionally needs a **capability declaration** per configured model, so `CoordinatorPipeline` can skip or refuse `VisionAnalyzerAgent` on a text-only model instead of failing at request time.

**0b — Per-provider work, given that split:**
- **OpenAI-compatible servers** (NVIDIA NIM, vLLM, TGI, LM Studio, llama.cpp): already work today with **no code change** — `Program.cs` reads `Llm:Provider`, selects the matching config section, and passes its `BaseUrl` to the OpenAI client. Adding a provider is adding a config section.
- **Azure OpenAI**: `Microsoft.Extensions.AI` ships an official Azure `IChatClient`/`IEmbeddingGenerator` connector — wire it as a new provider branch (deployment-based URL and `api-key` auth differ from plain OpenAI).
- **AWS Bedrock**: different wire format and SigV4 auth — needs its own implementation behind the chat and embedding seams.
- **Anthropic (native API)**: not OpenAI-compatible — needs its own **chat-and-vision-only** implementation (message format, system-prompt-as-top-level-field, and streaming events all differ). Its native prompt-caching API is also more capable than the OpenAI-compatible surface exposes, which matters for Phase 5.
- `LastTotalTokens` must be populated consistently by every implementation, since Phase 5's cost observability depends on it.

**0c — graphify's own backend selection is a second, independent provider surface.** `graphify extract --backend` accepts `gemini | kimi | claude | openai | deepseek | ollama`, configured through **environment variables** (`OPENAI_BASE_URL`/`OPENAI_MODEL`, `ANTHROPIC_BASE_URL`/`ANTHROPIC_MODEL`, or `GEMINI_API_KEY`), not through `Llm:*`. Consequences:
- The open-model goal is well served: `--backend openai` reaches any self-hosted OpenAI-compatible server, and `--backend claude` reaches Anthropic-compatible gateways (LiteLLM, proxies).
- **Bedrock and Azure are not graphify backends.** A Bedrock-only or Azure-only deployment cannot run semantic extraction or community labeling without a translation layer. See Outstanding Question **D2**.
- **Requirement**: the backend must derive graphify's environment from the same admin-facing provider config wherever the selected provider is compatible, so an operator configures a provider once. Where it is not compatible, the app must fail loudly at configuration time with a clear message, not silently at first ingestion.

#### Phase 1 (foundation): Unify KB sources through graphify, not bespoke connectors
Everything else builds on this. Instead of an `IKbConnector` per source type, use graphify's coverage and add a thin fetch step only where content isn't already a local file:
- **Code repos**: `graphify extract <clonedPath>` after `GitRepoSyncService.CloneOrPull`. AST extraction needs no LLM and no API key, so a code-only corpus is near-free per repo. This is the single largest token saving in the plan and it depends on nothing in Phase 0.
- **Local documents / PDF / MD / TXT** (today's `Documents` type): same input, but ingest through `graphify extract` instead of `DocumentChunker` + embeddings — graphify detects and handles docs, papers, images, and video in one corpus. Note this path **does** consume LLM tokens (semantic extraction), unlike the code path.
- **Websites and arbitrary URLs**: `graphify add <url>` fetches the page into `./raw` and folds it into the existing graph — no bespoke crawler.
- **Confluence**: no native graphify connector, so keep a small fetch step (Confluence REST API → page exported as markdown into the corpus folder), then extract it like any other doc. This is the one place a custom connector is still needed.
- **Fix the first-repo assumption**: `DocumentIngestionJobFactory`'s `Repos.FirstOrDefault()` resolution must become explicit — a KB source declares which repo it belongs to, or an absolute path. Doing this in Phase 1 is a prerequisite for Phase 4, not a cleanup.
- Keep `IVectorStoreService`/Chroma/Pinecone as a **secondary** index only where prose-similarity search beats graph traversal (e.g. "find KB articles worded like this ticket").

#### Phase 2: Clone-time KB pregeneration — the extraction output *is* the repo card
Directly attacks the token-cost problem in Phase 5, not just clone-time cost:
- Run `graphify extract <clonedPath>` right after `GitRepoSyncService.CloneOrPull` (`backend/SupportForge.Ingestion/Code/GitRepoSyncService.cs`). Its clustering step writes `graphify-out/` including `GRAPH_REPORT.md` and community labels — that report **is** the "what is this project" digest; there is no separate summarization step to design.
- **Community labelling is not free.** `extract`'s clustering (and the standalone `label` / `cluster-only` commands) calls an LLM backend to name communities. `--no-cluster` skips it and `label --missing-only` narrows it, but the Phase 2 digest carries a real, recurring token cost that must be budgeted and measured, not assumed to be zero. Only the AST pass is free.
- Store `graphify-out/` alongside the cloned repo under the existing repo cache root; point agents at `graphify query "<question>" --budget N` (default 2000 tokens) for capped answers instead of raw grep or full vector search.
- **`--wiki` and `--mcp` do not exist in the CLI** — both are `/graphify` skill-level flags. Agent access is via spawning `query`/`explain`/`path`/`affected` against `graphify-out/graph.json`. If a richer browsable digest is wanted later, `graphify tree` and `graphify export callflow-html` are the CLI-available equivalents.

#### Phase 3: Pluggable refresh (webhook / polling / manual, per source) — two refresh classes, not one
- Add a `RefreshMode` field (`Webhook | Polling | Manual`) to `KbSourceConfig` and `GitHubRepoConfig`, selectable per source.
- **Refresh splits by content type, and this is the correctness point of the phase:**
  - **Code** → `graphify update <path>`: re-extracts only changed *code* files, needs no LLM. Cheap enough to run on every push.
  - **Docs / Confluence / websites** → `update` does **not** cover them. They need a semantic re-extraction (`graphify extract`, LLM-backed and costly) or, for URLs, a re-`add`. Treat semantic refresh as a budgeted, rate-limited operation with its own schedule; do not attach it to every webhook.
  - `graphify check-update <path>` reports whether a semantic re-extraction is pending and is explicitly cron-safe — use it as the signal that decides when the expensive path runs, rather than re-extracting on a fixed timer.
- **Webhook**: `/webhooks/github` and `/webhooks/confluence`, signature-validated, enqueueing the appropriate refresh class for the changed repo or page.
- **Polling**: extend `IngestionBackgroundService` (already owns background ingestion and is the only file with logging) to check content hash / last-modified for `Polling` sources, then dispatch by class.
- **Manual**: `POST /resync/{sourceId}`, with the refresh class selectable so an operator can force a semantic rebuild.
- **Fourth trigger available for free**: `graphify hook install` adds post-commit / post-checkout git hooks that keep a locally-cloned repo's graph current without any webhook infrastructure. Worth offering for self-hosted deployments.
- graphify's own manifest-based change detection replaces a hand-rolled content-hash diff for the code path.

#### Phase 4: Multi-repo / cross-dependency RCA — graphify's cross-repo graph, not a hand-built one
A flat `Primary`/`Dependency` role doesn't fit a microservice project, where repos are peers and "primary" depends entirely on which service the current ticket is about. graphify already builds this kind of graph:
- **Prefer the global graph over ad-hoc merges.** `graphify global add <graph.json> --as <tag>` maintains a persistent multi-repo graph keyed by repo tag, with `global list` / `global remove` for lifecycle; `extract --global --as <tag>` folds a repo in at extraction time. This matches a project whose repo set changes over time better than re-running `merge-graphs` on every change. `merge-graphs` remains the one-shot alternative.
- **Cross-repo queries need an explicit graph path.** `query`/`path`/`explain`/`affected` all default to `graphify-out/graph.json`; merged and global graphs live elsewhere (`merged-graph.json`, `~/.graphify/global-graph.json`). Every cross-repo call must pass `--graph`, and forgetting it silently queries a single repo instead — a defect class worth a test.
- **Merge has a known correctness caveat**: `graphify diagnose multigraph` exists precisely because same-endpoint edges can collapse when graphs combine. Run it as a build-time gate on any merged or global graph before RCA trusts the result.
- **Entry-point resolution per ticket**: `TriageAgent` currently classifies intent into `kb_question | code_issue | code_question | screenshot_error | unclear` and normalizes unknown output to `unclear`. Service/repo resolution is a **second, separate classification** — explicit metadata when the source system provides it, else ticket text matched against repo/service names — and it needs the same "unresolved" fallback discipline, so an ambiguous match degrades to a whole-graph query rather than confidently starting from the wrong service.
- **Traversal**: `graphify path "<entryPoint>" "<suspect>"` for the shortest dependency path when there's a hypothesis; `graphify query "<question>" --dfs` to follow one thread deep across repo boundaries when there isn't; and **`graphify affected "<node>" --relation R --depth N`** for reverse traversal — blast-radius analysis, which is the RCA question "what else does this break?" and was missing from the earlier design.
- Community detection surfaces which repos cluster together even when no dependency link is configured — a **suggestion** signal for repos the admin forgot to add, without auto-adding them (keeps the manual-config decision).
- **Private-repo auth, stated precisely**: `GitRepoSyncService` reads one global `GitHub:Token` and applies it to every clone, while `GitHubRepoConfig.AccessTokenSecretName` is modeled but never read. Cross-org dependency repos frequently cannot share one token, so Phase 4 needs **per-repo credential resolution** wired to a secret store — not merely "wire the existing field."
- **Onboarding must accept multiple repos.** Ingestion already fans out per repo, but the Admin flow and configuration guide describe adding one. Lifting that limit is a Phase 4 prerequisite.
- Apply the no-code-exposure guardrails from `docs/plans/2026-07-29-001-feat-rca-answers-no-code-exposure-plan.md` **per repo/service** when formatting the answer — graphify's `source_location` citations identify which repo a fact came from, giving the guardrail filter a clean key.

#### Phase 5 (cross-cutting): Context rot, prompt caching, logging, observability
- **Context rot**: replace raw file dumps and broad vector search with `graphify query --budget N` (token-capped by construction) or `graphify explain "<node>"` for a single concept; agents escalate to full file reads only when the graph answer reports itself insufficient. Keep tool-facing output terse — compact facts plus citations, not prose — so Phase 1–2 savings aren't given back at the formatting layer.
- **Prompt caching**: structure agent prompts so static content (system prompt, the repo's `GRAPH_REPORT.md` digest) forms a stable prefix, and use provider prompt-caching for it. This pairs naturally with Phase 2, since the digest changes only on re-extraction, not per query. Note the caching surface is provider-specific, which is a further argument for the Phase 0a seam split.
- **Logging**: inject `ILogger<T>` into every `IAgent` implementation — log intent classification, graph-query hits, token counts, and per-step latency in `CoordinatorPipeline.cs`.
- **Observability**: **OpenTelemetry must be added, not "activated."** No project references any OpenTelemetry package; only `OpenTelemetry.Api` arrives transitively via `Microsoft.Extensions.AI` — an API surface with no SDK, no exporters, and no hosting integration, so nothing can be exported today. Phase 5 adds `OpenTelemetry.Extensions.Hosting` plus exporters (console for dev, OTLP for prod), following the existing swappable-provider config pattern.
- **Cost accounting**: track graphify's own per-run token cost as an observability input — KB build cost is as real as query cost. `graphify benchmark [graph.json]` measures token reduction versus a naive full-corpus approach, which is precisely the metric that justifies this plan; make it a recurring reported number, not a one-off.
- **Feedback loop, newly available**: `graphify save-result` records a Q&A outcome (`useful | dead_end | corrected`) into `graphify-out/memory/`, and `graphify reflect` aggregates those into a deterministic lessons document. This maps directly onto the existing Mark Useful / Escalate flow and `FeedbackEntry`, turning support feedback into graph-quality signal instead of a dead-end counter.

### Critical Files
- `backend/SupportForge.Agents/ILlmClient.cs`, `OpenAiLlmClient.cs` — the seam to split by capability (chat / embeddings / vision); add Anthropic and Bedrock implementations, wire Azure via the `Microsoft.Extensions.AI` Azure connector
- `backend/SupportForge.Api/Program.cs` — where `Llm:Provider` selects the config section today; extends to the per-capability provider flags and to deriving graphify's backend environment
- `backend/SupportForge.Api/appsettings.json` — ships `Llm:Provider = NvidiaNim` with `OpenAI`/`NvidiaNim` sections; new providers and the graphify backend config land here
- `backend/SupportForge.Core/Entities/KbSourceConfig.cs` — extend `KbSourceType`, add `RefreshMode`
- `backend/SupportForge.Core/Entities/GitHubRepoConfig.cs` — add `RefreshMode`; per-repo credential resolution replaces the unread `AccessTokenSecretName`. No hand-rolled `ServiceName`/`DependsOn` — graphify's graph carries the relationships
- `backend/SupportForge.Ingestion/Code/GitRepoSyncService.cs` — hook `graphify extract` after clone and `graphify update` after pulls; also the single-global-`GitHub:Token` site to make per-repo
- `backend/SupportForge.Ingestion/Code/CodeIngestionJobFactory.cs` — already fans out per repo; the graphify invocation slots in here
- `backend/SupportForge.Ingestion/Documents/DocumentIngestionJobFactory.cs` — remove the `Repos.FirstOrDefault()` path assumption; replace bespoke chunk/embed with graphify extraction; add the Confluence-to-markdown fetch step
- `backend/SupportForge.Ingestion/IngestionBackgroundService.cs` — extend for polling refresh (dispatching by refresh class), keep as the logging template
- `backend/SupportForge.Agents/CodeAnalyzerAgent.cs`, `TriageAgent.cs` — replace/augment vector search with `graphify query`/`path`/`explain`/`affected` process calls; add service resolution alongside the existing intent labels; repo-aware citations and per-repo guardrails
- `backend/SupportForge.Agents/CoordinatorPipeline.cs` — tracing spans, structured logging, vision-capability gating
- `docs/deployment/configuration-guide.md` — document new `Llm:*` values, the graphify backend environment, new KB source types, refresh modes, per-repo auth, multi-repo onboarding, and the Python/graphify runtime requirement. **Note**: this file has uncommitted local modifications documenting `Llm:*`; reconcile before editing

### Acceptance Examples (verification, per phase)
- **Phase 0**: with chat and embeddings pointed at *different* providers, one query completes end to end, `LastTotalTokens` is non-zero for each call, and switching either provider requires only config. A vision-incapable chat model causes `VisionAnalyzerAgent` to be skipped with a clear reason, not an exception. Startup with an incompatible graphify backend fails loudly at configuration time.
- **Phase 1**: run `graphify extract` over a test repo plus a Confluence-derived markdown file, and `graphify add <url>` for a website; confirm `graphify-out/graph.json` holds nodes from all three with correct `source_location`. Confirm a code-only extraction completes with **zero** LLM calls.
- **Phase 2**: clone a test repo, confirm `graphify-out/GRAPH_REPORT.md` and community labels exist and `graphify query "what is this project" --budget 2000` answers within budget. Record the token cost of community labelling as a measured number.
- **Phase 3**: push a code commit to a webhook-configured repo; confirm `graphify update` re-extracts only changed files (node/edge counts for untouched files stay stable) and makes no LLM call. Separately change a doc source and confirm `check-update` flags a pending semantic re-extraction and that the expensive path runs under its own budget, not on the code webhook.
- **Phase 4**: build a global/merged graph from two peer repos with a real cross-repo call; confirm `diagnose multigraph` is clean, `path`/`query --dfs` with an explicit `--graph` finds the cross-repo path, `affected` returns the correct blast radius, the answer cites the right repo, and guardrails apply per repo. Confirm an ambiguous service match degrades to a whole-graph query rather than picking wrong. Confirm two private repos in different orgs both clone with per-repo credentials.
- **Phase 5**: one end-to-end query produces a log line and a span per agent with token/latency data exported through a real OTLP or console exporter; a repeated query shows a prompt-cache hit; `graphify benchmark` reports a token-reduction figure; a Mark Useful click lands in `graphify-out/memory/` and appears in `graphify reflect` output.

### Outstanding Questions
Two decisions gate implementation and should be settled in the `ce-plan` pass:
- **D1 — seam granularity.** Split `ILlmClient` into three interfaces (chat / embeddings / vision), or keep one interface with capability flags and a composite implementation? This determines whether "Anthropic chat + NIM embeddings" is a config change or a code change. Recommendation: split, because the interface shape is what currently makes the stated provider goal unreachable.
- **D2 — graphify backend for Bedrock/Azure deployments.** graphify's `--backend` list excludes both. Options: (a) require an OpenAI-compatible gateway such as LiteLLM in front of Bedrock/Azure and point `--backend openai` at it; (b) restrict semantic extraction to compatible providers and document the limitation; (c) contribute a backend upstream. Recommendation: (a) — it costs no code here and keeps the "any provider" promise honest.

Non-blocking refinement candidates: the exact per-repo guardrail policy schema, choice of Bedrock connector library, and how the entry-point resolver scores ambiguous ticket-to-service matches.

### Appendix: Verification Findings (2026-08-01)
What the verification pass changed, and why. Numbered for traceability.

1. **Broken doc citations.** The plan cited `configuration-guide.md:7-20` for `Llm:Provider` and `:31` for the private-repo gap. At `bfcbad0` those lines are the old `OpenAI:ApiKey`/`VectorStore` table and a dark-mode deviation note. The `Llm:*` documentation exists only as an **uncommitted modification** in the main checkout. All line-number citations were replaced with section/file references, and the uncommitted state is now flagged.
2. **OpenTelemetry was never present.** "Activate the already-present package" was wrong: no `PackageReference` to any OpenTelemetry package exists. Only `OpenTelemetry.Api` appears transitively via `Microsoft.Extensions.AI`. Phase 5 now specifies adding the SDK and exporters.
3. **Skill syntax vs CLI syntax.** Every command was written in `/graphify` skill form (`graphify <path> --update`). The installed CLI is subcommand-based (`graphify update <path>`). Since the backend spawns a process, all commands were rewritten in CLI form.
4. **`--wiki` and `--mcp` do not exist in the CLI** (`graphify 0.9.9`). Phase 2's repo card and the "expose graphify via MCP" note both assumed skill-only surfaces. Replaced with the real outputs of `extract` plus `tree` / `export callflow-html`.
5. **`update` is code-only.** CLI help is explicit: "re-extract **code** files and update the graph (no LLM needed)." The claim that all refresh converges on one cheap command was false for docs, Confluence, and websites. Phase 3 now has two refresh classes and uses `check-update` as the gate for the expensive one.
6. **graphify has its own provider surface.** `extract --backend` accepts `gemini|kimi|claude|openai|deepseek|ollama` via environment variables, independent of `Llm:*` — and **excludes Bedrock and Azure**. Raised to Phase 0c and Outstanding Question D2.
7. **Cross-repo merge mechanics were unstated.** Added `global add/list/remove` as the preferred persistent mechanism, the explicit `--graph` requirement for querying merged graphs, and `diagnose multigraph` as a correctness gate against same-endpoint edge collapse.
8. **`ILlmClient` bundles chat and embeddings.** Anthropic has no embeddings API, so `Llm:Provider = Anthropic` breaks ingestion outright; `EmbedAsync` is additionally bound to the raw OpenAI wire format by a documented NIM workaround. Vision has the same problem in a weaker form. Phase 0a now requires a capability split — the largest substantive change in this revision.
9. **Private-repo auth was described imprecisely.** `GitRepoSyncService` already authenticates with one global `GitHub:Token`; the per-repo `AccessTokenSecretName` is modeled but never read. The requirement is per-repo credential resolution for cross-org repos, not "wire the secret store."
10. **"Multi-repo modeled but under-used" was wrong.** `CodeIngestionJobFactory` fans out per repo and `FreshnessCalculator` tracks per-repo staleness. The actual blockers are the one-repo onboarding flow and `DocumentIngestionJobFactory`'s `Repos.FirstOrDefault()` assumption — both now named as Phase 1/4 prerequisites.
11. **Phase 0 verification was untestable.** "Confirm equivalent answers from two providers" is not checkable against non-deterministic models. Replaced with contract assertions (mixed-provider run, non-zero token counts, config-only switching, loud failure on incompatible config).
12. **No runtime story for a Python dependency.** The repository contains no Dockerfile or container definition. Running graphify requires Python plus the `graphifyy` package on the backend host, and the .NET side needs process-spawn plumbing (working directory, timeouts, concurrency limits, non-zero-exit handling, and where `graphify-out/` lives relative to the repo cache root). This is unresolved and belongs in the `ce-plan` pass; it is called out here so it is not discovered during implementation.

Additional capabilities found during verification and folded into the plan as opportunities rather than gaps: `affected` (reverse traversal / blast radius, Phase 4), `save-result` + `reflect` (feedback loop onto the existing Mark Useful flow, Phase 5), `hook install` (git-hook refresh trigger, Phase 3), and `benchmark` (token-reduction measurement, Phase 5).
