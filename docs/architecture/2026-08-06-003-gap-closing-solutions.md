# Gap-Closing Solutions — Design Approach (no implementation yet)

Status: 2026-08-06, revised after a grilling session to pressure-test scope and priority. Companion docs:
[`2026-08-06-001-hld-system-design.md`](./2026-08-06-001-hld-system-design.md),
[`2026-08-06-002-gap-analysis.md`](./2026-08-06-002-gap-analysis.md).

Agreed sequencing: **Eval Harness → Security/Data Hotfixes → PRD Commitments (functional/cost items first,
access-control deferred) → Retrieval Quality → Frontend/UX.** This doc captures *what we'd build and why*,
at a design level, for every gap in that order. No code changes are made from this doc — it's the discussion
artifact to agree on before any implementation phase starts.

**Key decisions from the grilling session** (see rationale inline below): project creation is self-service
with auto-membership; self-registration stays open for now; RBAC and Entra ID migration are deferred to a
future access-control bucket and do **not** block the Critical project-ownership fix; deployment stays on
EC2+IIS (no Docker migration); Pinecone and Ollama are both in scope, specifically for provider flexibility;
"Send to Customer" and "Edit Draft" are dropped in favor of the existing Copy button.

---

## Phase A — Eval Harness

**Why first, even though it fixes nothing on its own:** every later phase (especially retrieval-quality and
any future prompt/model change) needs a way to know if it made things better or worse. Building this first
means later phases can each be checked against a baseline instead of shipped on faith.

### A1. Golden dataset + offline eval for the LLM-judge verifiers

- **Problem:** `KbResearcherVerifier`, `CodeAnalyzerVerifier`, `VisionAnalyzerVerifier` are prompted LLM
  judges with no measured precision/recall.
- **Proposed solution:** a small, versioned fixture set (JSON) per verifier, built from **multiple
  deliberately unrelated synthetic domains** (e.g. a fake e-commerce API, a fake healthcare intake form, a
  fake logistics dashboard — 4-5 invented domains, a handful of fixtures each), split across "should pass,"
  "should fail — irrelevant," and "should fail — empty," at minimum 20-30 cases per verifier to start.
- **Why domain-diverse and project-agnostic, not tied to one project (real or dogfooded):** this is a
  multi-org product — the verifier's job is to generalize the relevance-judgment skill across whatever
  arbitrary domain a given org's project turns out to be, not to master one project's content. A dataset
  bound to a single project (even our own) would validate the wrong thing. This also means A1 has **zero
  dependency on any project existing** — it can start immediately, independent of any customer or internal
  onboarding.
- **Design decision:** a standalone eval runner (separate console app or test project, `SupportForge.Evals`)
  replays each fixture through the real verifier prompt against the real LLM (not mocked) and reports
  agreement rate. Runs on-demand / in CI as a scheduled job, not on every request — an offline check, not a
  runtime gate. Kept outside `SupportForge.Agents` so it never becomes a runtime dependency.
- **Non-breaking:** zero production code touched — new project, new fixtures, nothing wired into
  `CoordinatorPipeline`.
- **Effort:** Medium (mostly curating domain-diverse fixtures; harness itself is small).

### A2. Feedback loop wiring

- **Problem:** `FeedbackController` writes to `IFeedbackRepository`; nothing reads it back.
- **Proposed solution:** two lightweight consumers, not a full ML pipeline:
  1. A periodic report (script or admin-page widget) correlating `FeedbackEntry.Rating` with the
     `VerificationResult.Confidence` the draft was produced with — surfaces cases where the system was
     confident but the user said "not useful," which is exactly where the eval dataset (A1) should grow.
  2. Feed "not useful" feedback entries into the golden dataset (A1) as new fixtures once real projects
     exist and their data has been reviewed — real disagreements become regression tests, additively, on
     top of the domain-diverse synthetic baseline (not a replacement for it).
- **Non-breaking:** additive read-only consumer of existing data; no change to `FeedbackController`'s write
  path or any agent.
- **Effort:** Small.

### A3. Lightweight groundedness check (design only — held for Phase D)

Deliberately **not** scheduled in Phase A. Changing `DrafterAgent`'s output-shaping logic is a behavior
change, not tooling — see Phase D3.

---

## Phase B — Security & Data Hotfixes

Each item below ships as its own small, independent PR — these are fixes to code that's already behaving
incorrectly, not new architecture. (Self-registration lockdown, originally scoped here, has been moved out —
see "Deferred to future access-control work" below.)

### B1. Project-ownership check (Critical)

- **Problem:** `ProjectId` is client-supplied with no check the caller belongs to that project. Compounding
  this: `Project` has no owner/creator concept at all today — `POST /api/projects` (`ProjectsController.cs:48-53`)
  is open to any authenticated user with nothing recording who created what.
- **Agreed design:** project creation stays **self-service** — any authenticated user can create a project
  via `POST /api/projects`, matching the PRD's "easy onboarding of new projects" objective, and the
  **creator automatically becomes that project's first member** at creation time. A `ProjectMembership`
  data shape (JSON-repo entry, consistent with the project's existing repository pattern) records
  user↔project membership; every `ProjectId`-scoped controller action checks membership via a shared guard
  (`IProjectAccessGuard`, implemented as an ASP.NET Core filter/attribute — one place to get right,
  consistent with the existing `[Authorize]` pattern) and returns `403` if the caller isn't a member.
- **Reserved for later:** admin-gated project creation (only Admins create projects, then grant membership
  explicitly) is a valid future option once RBAC exists, but isn't needed to ship this fix now — self-service
  creation with auto-membership is fully sufficient to close the data-leak gap on its own.
- **Non-breaking:** additive gate; every existing legitimate request (user querying a project they created)
  is unaffected once memberships are backfilled for any projects that already exist without an owner record.
- **Effort:** Medium (needs the membership data model, even a minimal one).

### B2. Global exception-handling middleware (High)

- **Proposed solution:** standard ASP.NET Core `IExceptionHandler` returning RFC 7807 `ProblemDetails`
  consistently, registered once in `Program.cs`. Existing controller-specific error handling (if any) keeps
  working; this only catches what currently falls through to the framework default.
- **Non-breaking:** purely additive middleware; doesn't change any controller logic, only what an *unhandled*
  exception returns to the client.
- **Effort:** Small.

### B3. JWT signing key fallback (Low, folded in here since we're touching auth)

- **Proposed solution:** fail fast at startup (`Program.cs`) if `Jwt:Key` is unset outside Development,
  instead of silently generating a random key.
- **Non-breaking:** dev/test unaffected; makes a real production misconfiguration loud instead of silent.
- **Effort:** Trivial.

### B4. Cross-repo Neo4j `MERGE` key fix (High)

- **Problem:** `GraphImportJob.cs:56` merges on `{id, projectId}`; two repos in one project with the same
  relative file path collide.
- **Proposed solution:** widen the merge key to `{id, projectId, repo}`. Requires a one-time backfill/rebuild
  of existing graphs for any project with >1 repo (safe: re-running ingestion is already idempotent).
- **Non-breaking:** existing single-repo projects see no behavior change; multi-repo projects get corrected
  data on next ingestion run.
- **Effort:** Small (query change) + one-time data migration consideration.

### B5. Orphaned chunk deletion (Medium-High)

- **Proposed solution:** on each `DocumentIngestionJob` run, diff the new chunk-ID set against what's stored
  for that source, and call `DeleteAsync` for IDs no longer present, alongside the existing upsert.
- **Non-breaking:** additive cleanup step in an existing job; only removes chunks that are already stale.
- **Effort:** Small-Medium.

### B6. Neo4j health check (Medium)

- **Problem:** `/health` covers Chroma (live query) and LLM (DI resolution only); Neo4j — a hard dependency
  for all code Q&A — has no check, so an outage is invisible until a query fails.
- **Proposed solution:** add a `Neo4jHealthCheck` following the same shape as the existing
  `VectorStoreHealthCheck` (bounded-timeout live query), registered alongside it in `Program.cs`.
- **Non-breaking:** purely additive health-check registration.
- **Effort:** Trivial.

---

## Phase C — PRD Commitments (functional/cost items first, access-control deferred)

Reordered from the original proposal: leads with capability/cost items, since access-control hardening
(RBAC, Entra ID) has been explicitly deprioritized — see "Deferred to future access-control work" below.
Each item is scoped as its own effort; further sequencing within this phase is a discussion for when we get
there.

1. **Pinecone implementation.** Design: add a second `IVectorStoreService` implementation
   (`PineconeVectorStoreService`) alongside `ChromaVectorStoreService`, selected via the existing
   `VectorStore:Provider` config switch that already anticipates this (`VectorStoreServiceCollectionExtensions.cs`)
   — the abstraction is already correctly shaped, this is "fill in the stub," not a redesign. **Purpose:**
   not a replacement for Chroma — the value is being able to switch providers per deployment/org, same
   rationale as Ollama below.
2. **Ollama (open-source, self-hosted) LLM provider.** Design: Ollama exposes an OpenAI-compatible `/v1`
   API, so it doesn't need a new `ILlmClient` implementation — it slots into the existing
   `BuildOpenAiCompatibleClient` path already shared by `"OpenAI"`/`"NvidiaNim"`
   (`LlmServiceCollectionExtensions.cs:66-69,154-170`). Concretely: add `"Ollama"` to `SupportedProviders`
   and its `switch` case (pointing `BaseUrl` at the local Ollama server, e.g. `http://localhost:11434/v1`),
   plus a config section (`Llm:Ollama:ChatModel`, `:EmbeddingModel`) for model selection (e.g. `llama3.1`,
   `nomic-embed-text`). One caveat worth validating before relying on it for the verifier judges: local
   open-source models are generally weaker at structured yes/no judgment than the current hosted models —
   Phase A's eval harness is exactly the tool to confirm judge quality holds if Ollama is used for
   verifiers, not just as a cheaper option for Drafter/Triage.
   - **Non-breaking:** purely additive `switch` case; existing providers unaffected.
   - **Effort:** Small (wiring) + Medium (validating output quality via Phase A before defaulting any
     agent to it).
3. **Model tiering.** Design: extend `ILlmChatClient`/agent DI so each agent can declare a model-size hint
   (e.g. Triage/verifiers → small/cheap model, Drafter → larger model), resolved per-agent instead of one
   shared client. Needs care: verifiers currently share the Drafter's model quality assumptions implicitly;
   changing their model requires re-running Phase A's eval harness to confirm judge quality holds at a
   cheaper tier. Pairs naturally with Ollama above (cheapest tier = local model) once judge quality is
   validated.
4. **Freshness webhooks + scheduled re-ingestion + staleness alerts.** Design: a GitHub webhook endpoint
   (new controller action) enqueuing an ingestion job on push, plus an `IHostedService` timer for
   non-webhook sources (Confluence, Documents) running on a configurable interval; staleness alert surfaces
   through the existing `FreshnessGateAgent`/`FreshnessController` path into the frontend (ties into Phase E).
5. **Metrics export / Application Insights.** Design: add `WithMetrics()` to the existing OpenTelemetry
   pipeline (tracing is already wired) and register an Azure Monitor/App Insights exporter alongside the
   current OTLP exporter — additive to `Program.cs`'s existing telemetry setup, not a replacement.
6. **LLM provider runtime fallback + resilience parity.** Design: two related fixes — (a) give
   `BedrockLlmClient` the same Polly retry+circuit-breaker shape already used by OpenAI/Anthropic, so
   resilience posture is consistent across providers; (b) introduce a fallback chain
   (`ILlmChatClient` → ordered list of providers, try next on circuit-open) for the highest-value agents
   first (Drafter), rather than all five/six agents at once. Depends on Phase A's eval harness to confirm a
   fallback provider's output quality is acceptable before relying on it in production.
7. **Ingestion durability / dead-letter handling.** Design: replace (or front) the in-memory
   `Channel<IIngestionJob>` with a durable queue — smallest viable option is persisting job state to the
   existing JSON-repo pattern already used elsewhere, so a crash mid-run can resume from last-known state on
   restart; permanently-failing jobs move to a dead-letter list surfaced in the admin UI instead of only
   appearing in logs. A message-broker-backed queue is the more scalable option if ingestion volume grows
   past what a single process handles — worth revisiting alongside the horizontal-scalability item (see
   "Deliberately deferred") rather than solved twice.
8. **Deployment automation — stays on EC2 + IIS.** Design: the manual IIS runbook
   (`docs/deployment/backend-runbook.md`/`frontend-runbook.md`) gets scripted, not replaced — a PowerShell
   deploy script driving the same `dotnet publish` → app-pool → IIS steps already documented, plus a real
   deploy job in `.github/workflows/ci.yml`. **Decision:** EC2+IIS is the confirmed target; the existing
   Docker path (CI-tested, used for local/CI only) is not being promoted to production — no migration.
   "Rollback" becomes a scripted folder-swap instead of a manual one.

---

## Phase D — Retrieval Quality

### D1. Chunking overlap + richer metadata + incremental re-ingestion

- **Proposed solution:** add a configurable overlap (e.g. 100-150 chars) between consecutive chunks in
  `DocumentChunker`, and attach `title`/`headingPath` metadata where the source format provides it (e.g.
  Confluence/Website connectors already parse structure that's currently discarded at chunk time). Bundled
  in the same phase: content-hash the source document before chunking, and skip re-chunk/re-embed entirely
  when the hash is unchanged — closes the "full re-chunk every run" inefficiency alongside the overlap fix
  since both touch `DocumentIngestionJob`'s chunking step.
- **Non-breaking:** only affects newly-ingested content; existing Chroma data is untouched until re-ingested.
  The hash-skip is a pure optimization — unchanged sources produce identical output, just faster.
- **Effort:** Small-Medium.

### D2. Prompt-injection structural defenses (Medium-High)

- **Problem:** untrusted KB/code snippets are interpolated directly into prompts with no structural
  delimiter — only a soft system-prompt instruction discourages the model from treating retrieved content
  as instructions.
- **Proposed solution:** wrap retrieved content in explicit delimiters (e.g. XML-style
  `<retrieved_context>...</retrieved_context>` tags) across `DrafterAgent`, `KbResearcherVerifier`,
  `CodeAnalyzerVerifier` prompt-building, with an explicit system-prompt instruction to treat anything
  inside those tags as data, never as instructions to follow. This is the standard structural mitigation —
  doesn't eliminate injection risk entirely, but is a meaningfully stronger defense than wording alone.
- **Design decision:** validate this doesn't regress answer quality using Phase A's eval harness before
  and after, since prompt structure changes can shift model behavior in unexpected ways.
- **Non-breaking:** prompt-text change only, no interface/contract change to any agent.
- **Effort:** Small, but needs eval-harness validation (depends on Phase A).

### D3. Groundedness check in `DrafterAgent`

- **Proposed solution:** after drafting, a second lightweight LLM call (or heuristic overlap check) verifies
  each factual claim in the draft is traceable to `KbSnippets`/`CodeSnippets`/`VisionFindings`; flags
  (lowers confidence) rather than blocks, mirroring the existing `LooksLikeLeak` guard's pattern of "detect
  and adjust confidence" rather than "hard fail."
- **Design decision:** measure this against Phase A's golden dataset before deciding whether it's a
  confidence-adjustment or a hard block — an aggressive groundedness check that fires on correct paraphrases
  would make answers worse, not better. This is exactly why it's sequenced after the eval harness exists.
- **Non-breaking:** additive check parallel to the existing leak guard; doesn't change the drafting prompt
  itself in the first iteration.
- **Effort:** Medium.

---

## Phase E — Frontend / UX

Scope trimmed from the original proposal — see "Deliberately deferred" for what was cut and why.

1. **Citations/sources display** — `MessageBubble`/`MessageThread` already receive `Sources` in the API
   response (`ChatQueryResponse`); this is a rendering gap, not a data gap — lowest-risk item in the whole
   plan.
2. **SSE hook + auth store tests, frontend step in CI** — pure test-coverage and CI-config additions.

---

## Deferred to future access-control work

Grouped together because they share the same rationale: none of them block the Critical fix (B1), and the
priority right now is functional/cost capability (Phase C) over access-control hardening.

- **RBAC (Support vs Admin roles)** — deferred. Its only current use in this plan was gating project
  creation (an option explicitly not taken — see B1) and admin-only actions the UI doesn't currently
  distinguish anyway.
- **Entra ID migration** — dropped, not just deferred. The custom JWT auth stays as the sole mechanism; no
  pluggable/OIDC work planned.
- **Closing self-registration** — stays open as-is. Revisit alongside RBAC if/when admin-gated flows are
  wanted.

When any of these becomes a priority again, each should get its own scoped design discussion — they're
meaningfully sized efforts, not something to reopen as an afterthought inside another phase.

## Deliberately deferred (not silently dropped)

- **Horizontal scalability** (in-process queue, in-memory state, `AddSingleton` DI) — the Gap Analysis rates
  this High long-term but explicitly notes it's acceptable at the PRD's stated "10+ projects on one box"
  scale. Solving it now would mean redesigning the ingestion queue and pipeline state around a distributed
  backend before there's a load problem to justify it. Revisit if/when project count or request volume
  approaches a ceiling; Phase C7's durable-queue redesign is the natural first step if that happens, since
  it already needs to leave the in-memory `Channel` behind.
- **Verifier retry cap hardcoded to 1** (Low) and **`.env.production` tracked in git** (Low) — intentionally
  not scheduled as standalone work; fold into whichever future PR already touches
  `KbResearcherVerifier`/`CodeAnalyzerVerifier`/`VisionAnalyzerVerifier` or the frontend deploy config,
  respectively.
- **"Send to Customer" one-click action** — dropped. Would require a new outbound integration (email or a
  ticketing-system API client) that doesn't exist anywhere in the codebase today — a distinct feature, not
  a UI addition. The existing Copy button is sufficient: engineers paste the answer into whatever channel
  they already use.
- **"Edit Draft" inline affordance** — dropped, same reasoning. Copy-then-edit-externally covers this.

## Summary table

| Phase | Focus | Ships as | Depends on |
|---|---|---|---|
| A | Eval harness + feedback loop | New tooling, zero prod code | Nothing |
| B | Security + data hotfixes | 6 independent small PRs | Nothing (B1 needs a minimal membership model) |
| C | PRD commitments — Pinecone, Ollama, model tiering, freshness, metrics, LLM resilience, ingestion durability, IIS deploy automation | 8 separate efforts, each scoped on arrival | A (tiering/fallback eval) |
| D | Retrieval quality | 3 PRs | A (D2/D3 need eval baseline) |
| E | Frontend/UX (citations + test coverage only) | 2 independent PRs | C's freshness work (for staleness alert UI) only |

Nothing here is implemented yet — this is the agreed design. Next step when you're ready: pick which single
item to scope into an actual implementation plan first.
