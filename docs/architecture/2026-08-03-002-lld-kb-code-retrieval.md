# LLD: KB and Code Retrieval Architecture

Status: reflects code on branch `worktree-fix-verifier-relevance` as of 2026-08-03.
Companion doc: [`2026-08-03-001-hld-kb-code-retrieval.md`](./2026-08-03-001-hld-kb-code-retrieval.md).

All file references are relative to `backend/`.

## 1. Knowledge Base pipeline

### 1.1 Ingestion

| Source type | Job | Notes |
|---|---|---|
| Local folder (`.md`/`.txt`) | `SupportForge.Ingestion/Documents/DocumentIngestionJob.cs` | Recursively reads `_folderPath`, filters to `SupportedExtensions`. |
| GitHub folder URL | `Documents/GitHubFolderIngestionJob.cs` | Pastes a raw `github.com` folder URL; parsed by `GitHubFolderUrl.cs`. |
| Confluence | `Documents/ConfluenceIngestionJob.cs` + `ConfluencePageFetcher.cs` | Uses `ConfluenceOptions` for auth/space config. |
| Website | `Documents/WebsiteIngestionJob.cs` | Crawls and extracts page text. |

All four converge on the shared **`KbVectorIndexer`** (`Documents/KbVectorIndexer.cs:14-50`):

1. `DocumentChunker.Chunk(text)` — max 1000 chars per chunk, sized to stay under NVIDIA NIM's 512-token
   embedding limit (`DocumentChunker.cs:9`).
2. `ILlmEmbeddingClient.EmbedAsync(chunk)` — one embedding call per chunk.
3. Build a `VectorDocument(Id, Text, Embedding, Metadata)`; `Id` = `SanitizeId(sourceRef)-{chunkIndex}`
   (`KbVectorIndexer.cs:48-49`, alnum-only to survive Chroma's id constraints).
4. `IVectorStoreService.UpsertAsync("{projectId}-kb", vectorDocs)` — batch upsert, only called if
   `vectorDocs.Count > 0`.

After indexing, each job calls `KbSourceSync.MarkSyncedAsync` to stamp `LastSyncedAt` on the matching
`project.KbSources` entry (matched by `Location`).

**Note:** ingestion here does *not* invoke `graphify extract` for KB content in the current query path — it
only chunks/embeds. (`GraphifyCliRunner` is still used elsewhere for docs in some source types per
`DocumentIngestionJob.cs:28-36`'s historical note, but the retrieval-relevant path for KB is purely the
Chroma vector index built by `KbVectorIndexer`.)

### 1.2 Storage: Chroma

`SupportForge.VectorStore/Chroma/ChromaVectorStoreService.cs` implements `IVectorStoreService` against
Chroma's v2 HTTP API:

- Collection path: `/api/v2/tenants/{tenant}/databases/{database}/collections`.
- `UpsertAsync` / `QueryAsync` / `DeleteAsync` operate on a collection **UUID**, resolved via a
  `get_or_create=true` POST to the collections endpoint first (`ResolveCollectionIdAsync`,
  `ChromaVectorStoreService.cs:87-95`) — Chroma's GET/DELETE-by-name routes differ from its
  upsert/query/delete-records routes, which require the UUID.
- `QueryAsync` request: `{ query_embeddings: [[...]], n_results: topK, where: metadataFilter }`; response is
  parsed from Chroma's parallel-array shape (`Ids[0]`, `Documents[0]`, `Distances[0]`, `Metadatas[0]`).
- Collection naming convention: `{projectId}-kb` for docs, `{projectId}-code` was the old naming (see §3.3 —
  no longer written to by the current code path).

### 1.3 Query-time retrieval: `KbSearchTool`

`SupportForge.Agents/Tools/KbSearchTool.cs:16-21`:

```
embedding = await _llm.EmbedAsync(query)
results   = await _vectorStore.QueryAsync($"{projectId}-kb", embedding, topK)
return results.Select(r => (r.Text, r.Metadata["source"]))
```

Called from `KbResearcherAgent.RunAsync` (`KbResearcherAgent.cs:19-49`):

- Gate: only runs for `context.Intent in {kb_question, code_issue, code_question}` — line 25.
- `topK = context.KbVerification.Attempts > 0 ? 10 : 5` — retry widens the search rather than changing
  the query itself (unlike the code path, which changes traversal strategy — see §2.3).
- Each result becomes a `KbSnippets` entry plus a `Sources` tuple `("KB: {filename}", source)`.

## 2. Code pipeline

### 2.1 Ingestion: AST extraction, no LLM

`SupportForge.Ingestion/Code/CodeIngestionJob.cs:33-53`:

1. `GitRepoSyncService.CloneOrPull(repoUrl, localCachePath, branch)` — clones on first sync, pulls thereafter.
2. `GraphifyCliRunner.RunAsync(localCachePath, null, ct, "extract", ".", "--no-cluster")` — runs `graphify
   extract` in the repo's working directory. `--no-cluster` deliberately keeps this AST-only (no LLM call for
   community/semantic labeling), so this step is free and safe to run on every sync regardless of frequency
   (comment at `CodeIngestionJob.cs:37-39`).
3. Stamps `LastSyncedAt` on the matching `project.Repos` entry.

Output lands at `graphify-out/graph.json` under the repo's working directory (implied by `CodeGraphMergeJob`'s
usage — see below).

### 2.2 Merge: per-project graph

`SupportForge.Ingestion/Code/CodeGraphMergeJob.cs:11-40` — a project can have multiple repos; `graphify query`
operates on a single graph file, so this job produces one project-scoped graph:

- 1 repo → `File.Copy(repoGraphPaths[0], projectGraphOutPath, overwrite: true)` (no CLI call needed).
- 2+ repos → `graphify merge-graphs <path1> <path2> ... --out <projectGraphOutPath>`.

Output path convention (from `GraphifyQueryTool.cs:20`): `{repoCacheRoot}/{projectId}/graphify-project/graph.json`.

### 2.3 Query-time retrieval: `IGraphifyQueryTool` / `GraphifyQueryTool`

Interface (`SupportForge.Agents/Tools/IGraphifyQueryTool.cs:10-13`) deliberately lives in `SupportForge.Agents`
(not alongside its implementation in `SupportForge.Ingestion`) so `CodeAnalyzerAgent`'s tests can mock it
without a project reference to `SupportForge.Ingestion` — same pattern as `ILlmEmbeddingClient` /
`IVectorStoreService`.

Implementation (`SupportForge.Ingestion/Graphify/GraphifyQueryTool.cs:18-31`):

```
graphPath = {repoCacheRoot}/{projectId}/graphify-project/graph.json
if !File.Exists(graphPath): return null   // no repos ingested yet — graceful no-op, not an error

args = ["query", question, "--graph", graphPath]
args += retrying ? ["--dfs", "--budget", "4000"] : ["--budget", "2000"]

output = await graphify.RunAsync(cwd, env: null, ct, args)
return (blank or "No matching nodes found.") ? null : output
```

Key design point: **retry changes the traversal, not just the budget.** A first attempt uses BFS-default with
a 2000-token budget; a retry switches to `--dfs` with a larger 4000-token budget specifically *because* a
retry re-running an identical BFS on an identical graph would deterministically return the identical output —
wasting an LLM judge call before failing final (comment at `GraphifyQueryTool.cs:24-26`). This is the code-path
analog of the KB path's `topK` widening on retry, but structurally different because graph traversal strategy,
not just result count, is the retry lever.

Called from `CodeAnalyzerAgent.RunAsync` (`CodeAnalyzerAgent.cs:19-49`):

- Gate: only runs for `context.Intent in {code_issue, code_question}` — line 25 (note: KB gate additionally
  includes `code_issue`/`code_question`, so both KB and code retrieval run in parallel for those two intents;
  see HLD §4 sequence diagram).
- `retrying = context.CodeVerification.Attempts > 0`.
- On non-null output: `CodeSnippets.Add(output)` (single entry, not multiple — graphify returns one formatted
  traversal result, not a ranked list) + `Sources.Add(("Code: project graph", projectId))`.

### 2.4 `GraphifyCliRunner`: the subprocess chokepoint

`SupportForge.Ingestion/Graphify/GraphifyCliRunner.cs` — every call into the `graphify` binary (extract,
merge-graphs, query) goes through this single class:

- **Concurrency cap:** `SemaphoreSlim(2, 2)` process-wide (`ConcurrencyGate`, line 16) — marked
  `ponytail:` as a fixed budget to revisit once measured against real CPU/LLM rate-limit pressure.
- **Argument safety:** arguments are passed via `ProcessStartInfo.ArgumentList` (OS argument array, never a
  shell string), so no shell-injection surface exists; `ValidateArgument` additionally rejects null bytes and
  embedded newlines as defense-in-depth against argument-injection into `graphify`'s own flag parser for
  externally-sourced values (ticket text, URLs) — `GraphifyCliRunner.cs:150-156`.
- **Timeout:** default 10 minutes, cancels and kills the process tree (`entireProcessTree: true`) on either
  caller cancellation or timeout — chosen deliberately (line 89-90) so a caller giving up doesn't leave an
  orphaned `graphify` process still burning CPU/LLM budget.
- **Secret redaction:** `Redact()` scrubs any environment value whose key contains `KEY`/`TOKEN`/`SECRET` out of
  captured stderr before it can reach a logged exception message — because a misconfigured/failing LLM
  provider can echo its own API key back on stderr (`GraphifyCliRunner.cs:109-129`).

### 2.5 `GraphifyBackendResolver`: provider config bridging

`SupportForge.Ingestion/Graphify/GraphifyBackendResolver.cs` — `graphify`'s own `--backend` selection
(`gemini|kimi|claude|openai|deepseek|ollama`) is a *second, independent* provider surface from the app's
`Llm:*` config. `Resolve()` derives graphify's subprocess environment from the app's configured `Llm:Provider`
so an operator configures a provider once:

- `OpenAI` / `NvidiaNim` → `OPENAI_BASE_URL` / `OPENAI_MODEL` / `OPENAI_API_KEY`.
- `Anthropic` → `ANTHROPIC_BASE_URL` / `ANTHROPIC_MODEL` / `ANTHROPIC_API_KEY`.
- `Azure` / `Bedrock` → not directly supported by graphify; requires `Graphify:Gateway:BaseUrl` (must be HTTPS —
  enforced, since the gateway is a trust boundary graphify's extraction output is taken on faith from) fronting
  it with an OpenAI-compatible gateway (e.g. LiteLLM). Fails loudly at config-resolution time rather than
  letting the first `graphify extract` fail obscurely.

## 3. Relevance verification (applies to both paths identically)

### 3.1 Why a judge exists at all

Both `KbSearchTool` (vector nearest-neighbor) and `GraphifyQueryTool` (graph traversal) can return results that
are non-empty but off-topic — vector search always returns its topK nearest neighbors with no similarity floor,
and a graph traversal can surface the closest-matching-but-still-irrelevant subgraph. Comment shared verbatim in
both verifiers: *"a non-empty result set doesn't mean the content is on-topic. Judge the top match: if even the
closest snippet isn't relevant, the rest (farther away) won't be either."*

### 3.2 `KbResearcherVerifier` / `CodeAnalyzerVerifier`

Both (`KbResearcherVerifier.cs`, `CodeAnalyzerVerifier.cs`) follow the identical shape:

```
if snippets.Count == 0:
    Fail("No {kind} snippets were retrieved...")
else:
    relevant = await JudgeAsync(context)   // 1 LLM call, system prompt asks for yes/no on the top item
    Passed if relevant else Fail("LLM judge found the top retrieved snippet not relevant to the query.")

Fail(v, reason):
    v.Status = v.Attempts < 2 ? FailedRetrying : FailedFinal
    v.Reason = reason
```

**Fixed in this branch (commit `a085e8c`, "always judge relevance of retrieved KB/code snippets"):** prior
logic only ran the judge when exactly one snippet was retrieved; any other count (0 excluded, but 2+) skipped
the judge and auto-passed. Current code (confirmed by direct read, not by trusting the commit message) always
judges the top-ranked item unconditionally whenever the snippet count is nonzero — `KbResearcherVerifier.cs:41-49`,
`CodeAnalyzerVerifier.cs:41-49`. This closes the gap where a multi-result-but-irrelevant KB/code retrieval would
have silently passed verification and reached the customer-facing draft.

### 3.3 Retry semantics

`VerificationResult.Attempts < 2` → `FailedRetrying`, else `FailedFinal`. `CoordinatorPipeline` wires each
verifier's `FailedRetrying` status back to its own specialist via a conditional edge
(`CoordinatorPipeline.cs:137-138`, `141-142`); anything else forwards to that branch's collector. This gives
each of KB and code retrieval **up to 2 attempts total** (1 initial + 1 retry) before the branch is marked
`FailedFinal` and proceeds to `Drafter` anyway — `Drafter`'s `ComputeConfidence` then reflects the failure in
the returned `Confidence` score rather than blocking the response (`DrafterAgent.cs:77-87`).

## 4. Fan-out / fan-in orchestration (context for retry behavior above)

Handled by `CoordinatorPipeline` (`SupportForge.Agents/CoordinatorPipeline.cs`) using the Microsoft Agent
Framework `Workflow` API — full detail already documented in [`docs/agentic-pipeline.md`](../agentic-pipeline.md).
Relevant to this pair of docs: KB and Code branches run **fully independently and in parallel** (`AddFanOutEdge`
from Triage to `[kbExec, codeExec, visionExec]`), each with its own retry loop, converging only at the
`AddFanInBarrierEdge` into `Merge` before `Drafter` runs. Neither retrieval path can see or influence the
other's results or retry decisions.

There is a second, manual (non-workflow) execution path for streaming: `ChatController.QueryStream` calls
`RunWithVerificationAsync` sequentially for KB then Code then Vision (`ChatController.cs:60-71`, `222-225`) —
functionally equivalent retry-once semantics, but sequential rather than parallel, because the Workflow API
doesn't expose per-token streaming output from `Drafter`.

## 5. Persistence: `IVectorStoreService` vs. graph files

| | KB | Code |
|---|---|---|
| Storage medium | Chroma (external service, HTTP) | Flat file on disk (`graph.json`) |
| Collection/file key | `{projectId}-kb` | `{repoCacheRoot}/{projectId}/graphify-project/graph.json` |
| Deletion path | `IVectorStoreService.DeleteAsync` / `DeleteCollectionAsync` | Not found in current code — see gap below |
| Update model | Full re-chunk-embed-upsert per KB source sync | `extract` overwrites per-repo graph; `merge-graphs` rebuilds project graph |

**Gap:** no `graphify` or file-deletion call was found paired with project/repo deletion in the code read for
this doc. If a project or repo is deleted, its `graph.json` may be orphaned on disk. Confirm against
`ProjectsController`'s delete path before relying on this doc for a cleanup task — not verified here.

## 6. Config surface relevant to this pipeline

- `Llm:Provider`, `Llm:{Provider}:*` — chat/embedding provider, also drives `GraphifyBackendResolver`.
- `Graphify:Gateway:BaseUrl` / `Graphify:Gateway:ApiKey` / `Graphify:Gateway:Model` — only required when
  `Llm:Provider` is `Azure` or `Bedrock`.
- `ChromaOptions` (`BaseUrl`, `Tenant`, `Database`) — Chroma connection, used only by the KB path.
- Repo cache root — passed into `GraphifyQueryTool` constructor (`_repoCacheRoot`), sourced from DI wiring in
  `Program.cs` (not re-verified line-by-line for this doc).

## 7. Follow-up: retire or update the superseded plan doc

`docs/plans/2026-08-01-001-brainstorm-kb-rag-infrastructure-plan.md` proposed making graphify "primary" for
both KB and code with Chroma demoted to "secondary" for both. As implemented, code retrieval has moved to
graphify exclusively (Chroma is no longer touched by `CodeAnalyzerAgent`/`CodeSearchTool` — that class no
longer exists), but **KB retrieval still uses Chroma as its only mechanism**, not a secondary one. The plan
doc's premise for KB is stale. Recommend either updating that doc's status to "code: done; KB: not pursued,
vector search remains primary" or marking it superseded by this doc pair, so a future reader doesn't assume KB
is mid-migration to graphify when it isn't.
