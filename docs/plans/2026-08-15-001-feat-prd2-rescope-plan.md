---
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-brainstorm
execution: code
deepened: 2026-08-15
---

# SupportForge AI — PRD-2.0 Re-Scoped Delivery Plan

## Goal Capsule

Deliver PRD-2.0's re-scoped feature set — Org foundation, RBAC-gated code detail, version-aware answers with commit lookup, an MCP-based Integration Hub, closed-loop feedback with escalation/handoff, bounded cross-repo blast radius, and real-time collaborative support sessions — as seven sequential sprints, each landing as its own PR from its own worktree, building on `master` as of this plan's writing (`0978ef4`).

## Context

PRD-2.0 (`doc/PRD-2.0.md`) proposed 8 features in 4 weeks, aimed at turning SupportForge AI into a
"Technical Support Intelligence Platform." A grounding pass against the actual codebase found that
6 of the 8 features assume infrastructure that doesn't exist (Entra ID/RBAC, a dependency graph
engine, inbound observability connectors, ticketing integrations, real-time collaboration), while
`doc/rag-pipeline-reliability-review.md` — which reads as a list of open retrieval bugs — turned out
to already be **fully fixed** on `master` (verified file-by-file: table conversion, image
captioning, PDF/DOCX/PPTX support, fault isolation, table-aware chunking, relevance floor,
determinism, table-quoting exception, and test coverage are all in place).

During scoping, three more load-bearing facts surfaced:
1. PRD-2.0 intentionally pivots SupportForge from `STRATEGY.md`'s external, self-serve small-company
   product to an internal-only support tool — that pivot is confirmed intentional, not a doc gap.
2. An `Org` entity already exists on an **unmerged** branch (`worktree-org-github-tokens`). A
   research pass during enrichment found that branch is only 2 commits ahead of its merge-base with
   `master` (17 behind `master`), with just 2 small conflicts on merge — i.e. cheaper to reuse than
   this plan assumes. **User decision (2026-08-15): re-implement from scratch against current
   `master` anyway**, per this plan's original framing — do not rebase/cherry-pick the existing
   branch. Sprint 0 below reflects that decision.
3. A Neo4j-backed code graph already exists (`GraphImportJob`, `GraphDbQueryTool`,
   `CodeGraphExtractor` in `SupportForge.Ingestion`) — real `imports` and `defines` edges, a
   fulltext-indexed query tool already wired into `CodeAnalyzerAgent`. It's single-repo-scoped
   today (and has one bug blocking multi-repo use — see Sprint 5), not the "no graph engine" this
   plan first assumed. 5.4 is includable on top of it at a realistic, bounded scope. 5.8 (real-time
   collaboration) has no equivalent head start — no SignalR anywhere — but the `Conversation`/
   `ChatMessage`/`IConversationRepository` entities it would broadcast already exist.

This plan sequences the resulting scope — trust-first, foundation before features — into sprints,
with UI work called out alongside each backend change rather than as an afterthought.

**Team/timeline assumption:** timeline is an internal target that can flex; sprints are 1-week
sized for sequencing clarity, not a hard commitment. Adjust cadence to actual team capacity.

**Repo/project map** (backend solution, `backend/`):
- `SupportForge.Core` — entities and JSON-file repositories (flat namespace, not `Entities/`; every
  entity has an `I<X>Repository`/`JsonFile<X>Repository` pair, e.g. `IProjectRepository`/
  `JsonFileProjectRepository`).
- `SupportForge.Agents` — pipeline (`AgentContext`, `CoordinatorPipeline`, `TriageAgent`,
  `KbResearcherAgent`, `CodeAnalyzerAgent`, `DrafterAgent`, `VisionAnalyzerAgent`) and
  `Tools/` (`ICodeGraphQueryTool`, etc.).
- `SupportForge.Ingestion` — `GitRepoSyncService`, `CodeIngestionJob`, `GitHubFolderIngestionJob`,
  `Graph/` (`GraphImportJob`, `GraphDbQueryTool`, `CodeGraphExtractor`).
- `SupportForge.VectorStore` — `Chroma/ChromaVectorStoreService`.
- `SupportForge.Api` — controllers, `Program.cs` (DI registration), `Identity/CustomUserStore.cs`.
- `SupportForge.Api.Tests` — xunit, one directory per concern (`Controllers/`, `Ingestion/`,
  `Agents/`, `Integration/`, `VectorStore/`).
Frontend (`frontend/src/`): `pages/`, `components/`, `hooks/`, `lib/`. Hook tests colocate as
`useX.test.ts` next to `useX.ts`.

## Deferred / explicitly out of scope for this plan

- **Entra ID SSO** — not required; the compliance driver was repo/data visibility, not login method.
- **Full observability connector (5.5) and ticketing API (5.6) implementations** — mechanism is
  decided (Integration Hub / MCP, see Sprint 3), but building specific Sentry/App Insights/Jira
  connectors is not in this plan; the Hub ships with GitHub as its proving connector only.
- **Invite-token employee onboarding** — Admin sets employee passwords directly instead; no token
  generation/expiry/email infrastructure.

### Deferred to Follow-Up Work

- Whether PRD-2.0's original success metrics (≥40% handling-time reduction, ≥85% escalation
  quality) still apply given scope changed in both directions — not blocking, needs a follow-up
  conversation with stakeholders once Sprint 4 ships.
- Rebasing/reusing `worktree-org-github-tokens` was considered and explicitly declined by the user
  in favor of a clean re-implementation (see Context, point 2) — noted here so the branch isn't
  silently revisited later without that context.

---

## Verification Contract

- `dotnet test` (all backend test projects) green after every sprint's unit(s).
- Frontend test suite (`npm test` / project's configured runner) green after every sprint's
  frontend unit(s).
- Each sprint's own manual walkthrough (listed per sprint below) passes before that sprint's PR.
- Cross-cutting end-to-end walkthrough (see below) passes once Sprint 6 lands.
- No regression in the RAG reliability fixes already on `master` — re-run the existing table/image/
  diagram/PDF eval fixtures (`SupportForge.Evals`) after Sprint 2 (which touches retrieval) and
  again at the end.

## Definition of Done

- All 7 sprints' Implementation Units complete, each landed as its own PR from its own worktree, in
  dependency order (Sprint 0 → 6).
- `dotnet test` and the frontend test suite green on `master` after each merge.
- The cross-cutting end-to-end manual pass (below) completes successfully after Sprint 6.
- No unresolved regression in existing RAG reliability eval fixtures.

---

## Sprint 0 — Org Foundation

**Why first:** every later sprint (RBAC scoping, Integration Hub credential storage) depends on
`Org` existing. Building it first avoids a retrofit.

### U1. Org and OrgMembership entities + repositories

**Goal:** Introduce `Org` and `OrgMembership` as first-class entities with JSON-file-backed
repositories, following the codebase's existing entity/repo pairing convention exactly.

**Requirements:** Org Foundation (Context point 2); unblocks Sprint 1 (RBAC) and Sprint 3
(Integration Hub credential storage).

**Dependencies:** none (first unit in the plan).

**Files:**
- `backend/SupportForge.Core/Org.cs` (new) — `Id`, `Name`, `CreatedAt`. No GitHub PAT field here —
  that arrives in Sprint 3 (U13) as part of the Integration Hub's credential model, not duplicated.
- `backend/SupportForge.Core/OrgMembership.cs` (new) — mirror `ProjectMembership.cs`'s shape
  (`sealed record OrgMembership(string OrgId, string UserId, DateTimeOffset CreatedAt)`), extended
  with whatever role/admin marker Sprint 1 (U4) needs — coordinate field naming with that unit so it
  isn't renamed later; if Sprint 1 lands after this unit, add the field there instead of guessing
  its shape now.
- `backend/SupportForge.Core/IOrgRepository.cs`, `JsonFileOrgRepository.cs` (new) — mirror
  `IProjectRepository`/`JsonFileProjectRepository`.
- `backend/SupportForge.Core/IOrgMembershipRepository.cs`, `JsonFileOrgMembershipRepository.cs`
  (new) — mirror the same pattern.
- `backend/SupportForge.Api/Program.cs` (modify) — DI registration for the two new repos, following
  the existing registration block for `IProjectRepository` etc.
- `backend/SupportForge.Api.Tests/JsonFileOrgRepositoryTests.cs`,
  `JsonFileOrgMembershipRepositoryTests.cs` (new).

**Approach:**
1. Copy `Project.cs`/`ProjectMembership.cs` field shape and `JsonFileProjectRepository`'s
   read/write/JSON-file-locking pattern verbatim for `Org`/`OrgMembership`.
2. Register both repos in `Program.cs` in the same DI block as the project repos.

**Patterns to follow:** `backend/SupportForge.Core/Project.cs`, `ProjectMembership.cs`,
`IProjectRepository.cs`, `JsonFileProjectRepository.cs` and their existing test file for the JSON
read/write/locking pattern.

**Test scenarios:**
- Create an org, confirm it round-trips through `JsonFileOrgRepository` (write then read back all
  fields).
- Add an `OrgMembership`, confirm `GetByOrgIdAsync`/equivalent lookup returns it.
- Concurrent writes to the same org file don't corrupt data (mirror whatever concurrency test
  `JsonFileProjectRepositoryTests` already has, if any).

**Verification:** `dotnet test` green for the two new test files; DI resolves both repos at
`Program.cs` startup without error.

---

### U2. Project.OrgId + backfill migration

**Goal:** Add a required `OrgId` to `Project`, with a startup migration that backfills existing
single-tenant data to one default org.

**Requirements:** Org Foundation; every project must belong to an org before Sprint 1's
project-scoped registration can work.

**Dependencies:** U1.

**Files:**
- `backend/SupportForge.Core/Project.cs` (modify) — add required `OrgId` field.
- `backend/SupportForge.Ingestion/` or `SupportForge.Api/` (new, location follows existing startup
  migration/hosted-service convention if one exists — check `Program.cs` for `IHostedService`
  registrations before placing this) — `ProjectOrgMigration.cs`: on startup, if any `Project` has no
  `OrgId`, create one default `Org` (if none exists) and backfill.
- `backend/SupportForge.Api/Program.cs` (modify) — wire the migration to run at startup, before the
  API starts serving.
- `backend/SupportForge.Api.Tests/ProjectOrgMigrationTests.cs` (new).

**Approach:**
1. Add `OrgId` as required (non-nullable) on `Project`.
2. Migration: idempotent — running it twice must not create two default orgs or double-backfill.
3. Run the migration synchronously at startup before any controller can serve a request, so no
   request ever observes a project without an `OrgId`.

**Test scenarios:**
- Fresh JSON store with zero projects: migration runs, no default org created (nothing to backfill).
- Store with N pre-existing projects lacking `OrgId`: migration creates exactly one default org and
  assigns all N projects to it.
- Migration run twice against an already-migrated store: no duplicate org, no-op on second run.
- Store with a project that already has `OrgId` set (simulating partial migration): migration
  leaves it untouched and only backfills the ones missing it.

**Verification:** integration test creating pre-migration projects, running the migration, and
confirming they backfill to a default org; `dotnet test` green.

---

### U3. OrgsController

**Goal:** Expose org listing and self-service create-or-update over HTTP.

**Requirements:** Org Foundation; self-service org creation is the entry point Sprint 1's
`POST /api/auth/register` will call into.

**Dependencies:** U1, U2.

**Files:**
- `backend/SupportForge.Api/Controllers/OrgsController.cs` (new) — `api/orgs`: `GET` list orgs the
  caller belongs to; `POST` create-or-update (self-service — creating an org auto-joins the creator
  as its Admin, per Sprint 1's role model in U4; if U4 hasn't landed yet, stub the admin-role
  assignment as a TODO the next unit fills in, don't invent a role field here).
- `backend/SupportForge.Api.Tests/Controllers/OrgsControllerTests.cs` (new).

**Approach:** Follow `ProjectsController`'s existing list/create-or-update endpoint shape and
authorization pattern exactly.

**Patterns to follow:** `backend/SupportForge.Api/Controllers/ProjectsController.cs`.

**Test scenarios:**
- Authenticated user with no org memberships: `GET api/orgs` returns empty list.
- User creates an org via `POST`: response includes the new org, and the caller is now a member
  (auto-joined).
- User lists orgs after creating two: both appear, scoped to that caller only (no leakage of other
  users' orgs).

**Verification:** `dotnet test` green; manual `curl`/Postman round-trip creating an org and listing
it back.

---

**Frontend:** No new user-facing surface yet — Org is invisible infrastructure until Sprint 1
exposes org-scoped registration. Skip UI here; avoid building a page for a concept with no user
action yet.

**Sprint 0 verification:** unit tests for `JsonFileOrgRepository`/`JsonFileOrgMembershipRepository`,
an integration test creating an org and confirming existing pre-migration projects backfill to a
default org, `dotnet test` green.

---

## Sprint 1 — RBAC: Roles, Scoped Registration, Code-Detail Gating

### U4. AppUser.Role + two-tier registration

**Goal:** Add org-wide roles to `AppUser` and split registration into open self-service
(org+admin signup) vs. Admin-gated employee registration with explicit project scope.

**Requirements:** RBAC roles and scoped registration.

**Dependencies:** U3 (org creation must exist for register-flow to create one).

**Files:**
- `backend/SupportForge.Core/AppUser.cs` (modify) — add `Role` (`L1`/`L2`/`L3`/`Admin`), one role
  per user (org-wide, not per-project).
- `backend/SupportForge.Api/Controllers/AuthController.cs` (or wherever `POST /api/auth/register`
  currently lives — confirm exact path before editing) (modify) — keep this endpoint **open
  indefinitely** (multi-tenant self-service): creates a user, creates a **new** `Org` (via U3's
  `OrgsController` logic or the same underlying service), makes the user that org's `Admin`,
  auto-joins them to it. One org per admin.
- `backend/SupportForge.Api/Controllers/OrgsController.cs` (modify) — add
  `POST /api/orgs/{orgId}/employees`, `[Authorize(Roles="Admin")]`: Admin supplies username,
  password (Admin sets/shares it directly — no invite-token flow), `Role` (L1/L2/L3), and an
  explicit list of `ProjectId`s. Creates the `AppUser`, sets `Role`, and adds a `ProjectMembership`
  row per listed project (replacing project auto-join for employees — only org-creating Admins
  auto-join their own projects).
- `backend/SupportForge.Api.Tests/Controllers/AuthControllerTests.cs`,
  `OrgsControllerTests.cs` (modify/extend).

**Approach:**
1. `ProjectsController`/`ChatController`/retrieval-facing endpoints keep enforcing membership as
   today (unchanged mechanism) — RBAC adds *role*, not a new visibility mechanism.
2. Self-service register path and employee-register path are two distinct actions on two distinct
   endpoints, not one endpoint with a branching flag — keeps authorization boundaries obvious.

**Patterns to follow:** existing `AuthController`/`CustomUserStore.cs` for user creation;
`ProjectMembership` creation pattern from wherever project auto-join currently happens.

**Test scenarios:**
- Self-service register: new user + new org created, user is that org's Admin, auto-joined.
- Admin registers an employee with role `L2` and 2 project IDs: user created with `Role=L2`, exactly
  2 `ProjectMembership` rows, no org auto-join beyond the listed projects.
- Non-Admin caller hits `POST /api/orgs/{orgId}/employees`: 403.
- Admin registers an employee for a project outside their own org: rejected (org-scoped check).
- Duplicate username on either registration path: rejected with existing conflict behavior.

**Verification:** integration tests for both registration flows; `dotnet test` green.

---

### U5. FeedbackEntry.UserId

**Goal:** Stamp feedback submissions with the submitting user's id.

**Requirements:** avoids a backfill migration when Sprint 4 reworks feedback.

**Dependencies:** none (independent of U4, can land in parallel).

**Files:**
- `backend/SupportForge.Core/FeedbackEntry.cs` (modify) — add `UserId` alongside existing
  `ProjectId`.
- `backend/SupportForge.Api/Controllers/FeedbackController.cs` (modify) — `Submit` stamps the
  caller's id.
- `backend/SupportForge.Api.Tests/Controllers/FeedbackControllerTests.cs` (modify/extend).

**Approach:** Straight field addition; no migration needed for existing entries (they're historical
records, not read back by anything that requires `UserId` yet).

**Test scenarios:**
- Submit feedback as an authenticated user: stored entry's `UserId` matches the caller.
- Existing feedback-read paths (if any) don't break on entries lacking `UserId` (older data).

**Verification:** `dotnet test` green.

---

### U6. Role-gated code-detail response field

**Goal:** Add a new chat-response field carrying `CodeAnalyzerAgent`'s raw findings
(paths/line ranges/snippets), server-side gated to `L2`/`L3`/`Admin` — `L1` never receives it,
regardless of what the agent found. `DrafterAgent`'s customer-facing answer stays code-blind for
every role, unchanged.

**Requirements:** RBAC code-detail gating — the core trust mechanism of this sprint.

**Dependencies:** U4 (needs `Role` to gate on).

**Files:**
- `backend/SupportForge.Agents/AgentContext.cs` (read-only reference — code findings already
  accumulate here via `CodeSnippets`; no change needed to accumulation).
- Chat response DTO (wherever `ChatController`'s response type is defined — locate exact file before
  editing) (modify) — add an optional code-detail field.
- `backend/SupportForge.Api/Controllers/ChatController.cs` (modify) — populate the field only when
  the caller's `Role` is `L2`/`L3`/`Admin`; omit entirely (not null-but-present) for `L1`.
- `backend/SupportForge.Agents/DrafterAgent.cs` — **no change** to its system prompt's
  "never mention file/path/line/repo" rule; explicitly out of scope for this unit.
- `backend/SupportForge.Api.Tests/Controllers/ChatControllerTests.cs` (modify/extend).

**Approach:** Gate at the controller/response-assembly layer, not inside the agent pipeline — the
pipeline computes full detail regardless of role (this precedent is reused directly in Sprint 4's
escalation caching), only the HTTP response shapes what's visible.

**Test scenarios:**
- L1 caller asks a code-shaped question where `CodeAnalyzerAgent` finds matches: response has no
  code-detail field (not an empty one — absent).
- L3 caller asks the same question: response includes the code-detail field with paths/lines/
  snippets.
- L1's answer text itself (the drafted response) contains no file/path/line mentions regardless of
  what the agent found — confirms `DrafterAgent`'s existing rule is untouched.

**Verification:** integration test confirming L1 never receives the code-detail field even when
`CodeAnalyzerAgent` found matches; `dotnet test` green.

---

### U7. Audit logging for role/project-grant changes

**Goal:** Log who changed a role or project grant, when, and what.

**Requirements:** RBAC auditability.

**Dependencies:** U4.

**Files:**
- `backend/SupportForge.Api/Controllers/OrgsController.cs` (modify) — log at the employee-register
  and any future role-change action.
- `backend/SupportForge.Api/Program.cs` (reference only) — reuse whatever structured-logging
  pattern the existing `ILogger` usage already follows; no new logging subsystem.

**Approach:** One `ILogger.LogInformation` (or equivalent existing level) call per grant/role
change, structured with actor, target user, and change description — matching whatever fields
existing log calls in this codebase already use.

**Test scenarios:**
- Employee registration triggers exactly one audit log entry with actor=Admin's id,
  target=new employee's id, and role/project-grant details.

**Verification:** manual log inspection during the Sprint 1 walkthrough; no new test infra needed
beyond confirming the call site exists and doesn't throw.

---

### U8. Frontend: AdminPage Employees section + RequireRole

**Goal:** Admin UI to list/register employees; a reusable role gate for Admin-only UI.

**Requirements:** RBAC frontend surface.

**Dependencies:** U4.

**Files:**
- `frontend/src/pages/AdminPage.tsx` (modify) — add an "Employees" section — list org employees
  (name, role, project scope), a form to register a new employee (username, password, role
  dropdown, multi-select project scope from the org's projects), calling
  `POST /api/orgs/{orgId}/employees`. Visible only to `Admin` role (client-side gate mirroring the
  server-side one — server remains the actual enforcement point). `AdminPage.tsx` is already ~365
  lines before this change — keep the new section as a sub-component if it pushes the file past a
  comfortable size, following whatever sectioning pattern the file already uses.
- `frontend/src/components/RequireRole.tsx` (new) — mirrors `RequireAuth.tsx`'s pattern for a
  role-based route/section guard.
- `frontend/src/hooks/useRegisterEmployee.ts` (new) — mirrors `useCreateProject.ts`'s
  `useMutation` + `apiClient.post` + `queryClient.invalidateQueries` shape.
- `frontend/src/hooks/useRegisterEmployee.test.ts` (new).

**Patterns to follow:** `frontend/src/hooks/useCreateProject.ts`, `frontend/src/components/
RequireAuth.tsx` (or wherever it lives — confirm path).

**Test scenarios:**
- `useRegisterEmployee` hook: successful mutation invalidates the employees query.
- `RequireRole` renders children only when the auth context's role matches; renders nothing (or a
  fallback) otherwise.

**Verification:** hook test green; manual UI walkthrough covered in Sprint 1's overall verification.

---

### U9. Frontend: auth context Role + CodeDetailPanel

**Goal:** Surface the logged-in user's `Role` from auth context; render the new code-detail field
when present.

**Requirements:** RBAC frontend surface for the code-detail gating in U6.

**Dependencies:** U6, U8.

**Files:**
- `frontend/src/hooks/useLogin.ts` (or the auth context file — confirm exact path) (modify) —
  surface `Role`.
- `frontend/src/components/CodeDetailPanel.tsx` (new) — renders the code-findings field when
  present in the chat response (only present for L2/L3/Admin, per backend gating) — a collapsible
  panel alongside `MessageBubble.tsx`, not inline in the answer text.
- `frontend/src/pages/ChatPage.tsx` or `frontend/src/components/MessageBubble.tsx` (modify) — wire
  in `CodeDetailPanel` conditionally on the response field's presence.

**Test scenarios:**
- `CodeDetailPanel` renders nothing when the response has no code-detail field.
- `CodeDetailPanel` renders paths/lines/snippets when the field is present.

**Verification:** component test green; manual UI walkthrough.

---

**Sprint 1 verification:** integration tests for the two-tier registration flow, a test confirming
L1 never receives the code-detail field even when `CodeAnalyzerAgent` found matches, manual UI
walkthrough: sign up as a new org Admin, register an L1 and an L3 employee, log in as each, confirm
the L1 chat view has no code-detail panel and the L3 view does.

---

## Sprint 2 — Trust Payoff: Version-Aware Answers + Screenshot→Code+Commits

### U10. Version/Config threading through the query pipeline

**Goal:** Add optional `ProductVersion` and `Config` (key-value) to the chat request; use them as a
retrieval bias and have the drafted answer state the version it applies to.

**Requirements:** version-aware answers.

**Dependencies:** none (independent of Sprint 1; can run in parallel with it if scheduling allows,
though sequential is safer given both touch `AgentContext`/`ChatController`).

**Files:**
- `backend/SupportForge.Agents/AgentContext.cs` (modify) — add `ProductVersion`/`Config` fields
  (currently absent, confirmed).
- Chat request DTO (`ChatController`'s request type — confirm exact file) (modify) — add optional
  `ProductVersion`/`Config` fields.
- `backend/SupportForge.Agents/KbResearcherAgent.cs` (modify) — thread `ProductVersion`/`Config`
  into its retrieval call as a metadata filter/bias, reusing `IVectorStoreService.QueryAsync`'s
  existing `metadataFilter` parameter (`IReadOnlyDictionary<string,string>?`) — no new retrieval
  mechanism needed, this parameter already exists and is exercised by `RagPipelineE2ETests`.
- `backend/SupportForge.Agents/DrafterAgent.cs` (modify) — system prompt: add the
  version-disclaimer instruction (state version answer applies to; flag mismatch/missing version).
- `backend/SupportForge.Core/FeedbackEntry.cs` or wherever query logging persists (confirm exact
  file) (modify) — persist `ProductVersion`/`Config` with each query for analytics.
- `backend/SupportForge.Api.Tests/Agents/` or `SupportForge.Evals` fixtures (modify/extend) — a
  version-tagged eval case.

**Approach:**
1. `ProductVersion`/`Config` flow: request DTO → `AgentContext` → `KbResearcherAgent`'s
   `metadataFilter` → `DrafterAgent` prompt.
2. Missing version: `DrafterAgent` still answers but flags the missing-version state per its updated
   prompt instruction.

**Patterns to follow:** existing `metadataFilter` usage in `RagPipelineE2ETests.cs`.

**Test scenarios:**
- Query with a matching `ProductVersion`: retrieval prefers version-tagged sources over
  version-mismatched ones (extend `SupportForge.Evals` fixtures with a version-tagged case).
- Query with no `ProductVersion`: answer flags the missing-version state per the updated prompt.
- Query with a `ProductVersion` that has no matching sources: answer flags the mismatch.

**Verification:** eval-set run confirming version-specific sources are preferred when a version is
supplied; a test for the missing-version path; `dotnet test` green.

---

### U11. CommitLookupTool: recent commits + PR/issue links

**Goal:** Extend code-location matches with recent commit history (author, date, message) and
resolved PR/issue links.

**Requirements:** screenshot→code+commits trust payoff.

**Dependencies:** U6 (this extends the code-detail result shape U6 introduced).

**Files:**
- `backend/SupportForge.Agents/Tools/CommitLookupTool.cs` (new) — shells out to the already-cloned
  local repo via `LibGit2Sharp` (same pattern `GitRepoSyncService` already uses) for `git log` on
  the matched file/line range.
- `backend/SupportForge.Agents/Tools/CommitLookupTool.cs` (same file, or a small sibling) — plain
  `HttpClient` call to the GitHub REST API (reusing the existing `GitHub:Token` config) specifically
  to resolve PR/issue links commit messages don't reliably carry. Standalone REST call, not routed
  through Sprint 3's Integration Hub — keeps this sprint's diff small; swappable onto the Hub's
  GitHub connector later without changing the requirement.
- `backend/SupportForge.Agents/VisionAnalyzerAgent.cs`, `CodeAnalyzerAgent.cs` (modify) — extend the
  code-location match result to include `CommitLookupTool`'s output.
- `backend/SupportForge.Api.Tests/Agents/CommitLookupToolTests.cs` (new).

**Patterns to follow:** `backend/SupportForge.Ingestion/Code/GitRepoSyncService.cs` for
`LibGit2Sharp` usage; existing `GitHub:Token` config resolution.

**Test scenarios:**
- Matched file/line with real commit history: returns author/date/message for the most recent
  relevant commits.
- Commit message references a PR/issue number resolvable via GitHub REST: link included.
- GitHub REST call fails (rate limit, network): tool degrades gracefully, still returns local
  `git log` data without the PR link, doesn't fail the whole response (consistent with existing
  fault-isolation pattern used elsewhere in the pipeline).

**Verification:** `dotnet test` green; manual walkthrough uploading a screenshot with a real error,
confirming commits + working PR links appear for L3.

---

### U12. Frontend: version input + commits rendering

**Goal:** Query-form version/config input; render the version disclaimer and recent-commits list.

**Requirements:** version-aware + commit-lookup frontend surface.

**Dependencies:** U10, U11, U9 (renders inside `CodeDetailPanel`).

**Files:**
- `frontend/src/pages/ChatPage.tsx` (modify) — add a version field (dropdown of known versions +
  free-text fallback) and an optional key-value config input to the query form, above/near
  `ScreenshotDropzone`. "Known versions" source: start with free-text only — there's no existing
  version registry found in the codebase; if a per-project version list is wanted, confirm with
  whoever owns KB source config before building a dropdown — don't invent a version-list data model
  speculatively.
- `frontend/src/components/MessageBubble.tsx`, `MessageThread.tsx` (modify) — render the version
  disclaimer/mismatch warning prominently in the answer.
- `frontend/src/components/CodeDetailPanel.tsx` (modify, from U9) — for L2/L3, render the
  recent-commits list (author, date, message, PR link) under the matched file/line entries.
- `frontend/src/components/ConfidenceBadge.tsx` — **reuse as-is** for the existing top-3 file-match
  confidence display, no new component needed (already exists for this exact purpose).

**Test scenarios:**
- Version-tagged query renders the version disclaimer in the answer.
- Missing-version query renders the missing-version warning.
- L3 view of a code-shaped answer shows the commits list with working links; L1 view shows neither
  code detail nor commits.

**Verification:** component tests green; manual walkthrough per Sprint 2's overall verification.

---

**Sprint 2 verification:** eval-set run confirming version-specific sources are preferred when a
version is supplied, a test for the missing-version UI warning path, manual walkthrough: ask a
version-tagged question, confirm the answer states its version; upload a screenshot with a real
error, confirm L3 sees matched files + recent commits with working PR links, confirm L1 sees only
the customer-safe answer. Re-run existing RAG reliability eval fixtures to confirm no regression
from the retrieval changes in U10.

---

## Sprint 3 — Integration Hub (MCP) Foundation

**Why this shape:** replaces PRD section 7's bespoke `IObservabilityConnector`/`ITicketingProvider`
interfaces with one general mechanism — connect an MCP server once, reuse the same admin UI and
credential model for GitHub today and Atlassian/Notion/Linear/observability tools later, instead of
hand-rolling a new interface per SaaS integration.

### U13. Org.McpConnections + credential model

**Goal:** Add a connections collection to `Org`, JSON-file-backed, credential stored as a plain
field for now.

**Requirements:** Integration Hub credential storage.

**Dependencies:** U1 (Org must exist).

**Files:**
- `backend/SupportForge.Core/Org.cs` (modify) — add `List<McpConnection> Connections`.
- `backend/SupportForge.Core/McpConnection.cs` (new) — server identity, credential (plain field —
  matches the plaintext-for-now / `ISecretResolver`-later precedent established on the
  `worktree-org-github-tokens` branch's `Org.GitHubAccessToken`; do not build new encryption infra
  in this sprint), enabled-tools allowlist.
- `backend/SupportForge.Core/JsonFileOrgRepository.cs` (modify, from U1) — persist the new field.

**Test scenarios:**
- Add an MCP connection to an org, confirm it round-trips through `JsonFileOrgRepository`.
- Multiple connections on one org (e.g. GitHub + a future server) round-trip independently.

**Verification:** `dotnet test` green.

---

### U14. McpConnectionsController

**Goal:** Admin-facing CRUD for MCP connections.

**Requirements:** Integration Hub admin surface.

**Dependencies:** U13, U4 (needs `Role`/`[Authorize(Roles="Admin")]`).

**Files:**
- `backend/SupportForge.Api/Controllers/McpConnectionsController.cs` (new) —
  `api/orgs/{orgId}/mcp-connections`, `[Authorize(Roles="Admin")]`: list supported MCP server types
  (a small hardcoded catalog to start — GitHub only, this sprint), connect (store credential +
  chosen tool allowlist), disconnect.
- `backend/SupportForge.Api.Tests/Controllers/McpConnectionsControllerTests.cs` (new).

**Patterns to follow:** `OrgsController.cs`'s employee-registration endpoint for the
`[Authorize(Roles="Admin")]` + org-scoping pattern.

**Test scenarios:**
- Admin connects GitHub with a PAT + tool allowlist: connection stored, retrievable via list.
- Non-Admin caller: 403.
- Admin disconnects: connection removed.

**Verification:** `dotnet test` green.

---

### U15. McpToolInvoker + GitHub PAT precedence migration

**Goal:** Add the `ModelContextProtocol` client SDK; build the enforcement point that validates
allowlist + role gating before invoking an MCP tool; migrate GitHub PAT resolution to prefer a
connected Org connection, falling back to the existing global config.

**Requirements:** Integration Hub enforcement; keeps existing single-org deployments working
unmodified.

**Dependencies:** U13, U14.

**Files:**
- `backend/SupportForge.Agents/SupportForge.Agents.csproj` (modify) — add `ModelContextProtocol`
  client SDK reference (confirmed: not currently referenced anywhere in the solution).
- `backend/SupportForge.Agents/Tools/McpToolInvoker.cs` (new) — given an org's connection + a tool
  name: validates the tool is in that connection's allowlist, checks the caller's role against a
  role-gating table for that tool, then invokes it via the MCP client. This is the single
  enforcement point both layers (static allowlist + role gating) go through.
- `backend/SupportForge.Ingestion/Code/GitRepoSyncService.cs`,
  `CodeIngestionJob.cs`, `GitHubFolderIngestionJob.cs` (modify) — a connected GitHub MCP connection
  on an `Org` takes precedence when present; existing global `GitHub:Token` config remains the
  fallback default. Don't force every deployment to reconnect immediately.
- `backend/SupportForge.Api.Tests/Agents/McpToolInvokerTests.cs` (new).

**Test scenarios:**
- Tool not in the connection's allowlist: rejected regardless of caller role.
- Tool in the allowlist but not permitted for the caller's role: rejected.
- Tool in the allowlist and permitted for the role: invoked successfully.
- Org with no MCP connection: ingestion falls back to global `GitHub:Token` config unchanged.
- Org with a connected GitHub MCP connection: ingestion uses the connection's credential instead.

**Verification:** unit tests for `McpToolInvoker`'s allowlist + role-gating enforcement; an
integration test connecting a GitHub MCP connection and invoking a commit-lookup tool through it
end-to-end; `dotnet test` green.

---

### U16. Frontend: Connected Apps section

**Goal:** Admin UI to connect/manage MCP servers.

**Requirements:** Integration Hub frontend surface.

**Dependencies:** U14.

**Files:**
- `frontend/src/pages/AdminPage.tsx` (modify, or a new page — `AdminPage.tsx` is already ~365 lines
  before Sprint 1's additions; check its length at this point in sequencing before deciding) —
  "Connected Apps" section: lists supported MCP servers (start: GitHub), a connect flow (credential
  input appropriate to the server — PAT for GitHub), per-connection tool checkboxes with tooltips
  explaining what each tool does.
- `frontend/src/hooks/useMcpConnections.ts` (new) — mirrors `useOrgs.ts`/`useProjects.ts` shape
  (confirm exact hook shape from whichever of those exists on `master` at this point — Sprint 0's
  `OrgsController` doesn't necessarily ship a matching frontend hook, so this may be the first
  `useOrgs.ts`-shaped hook; if so, follow `useProjects.ts` instead).
- `frontend/src/hooks/useMcpConnections.test.ts` (new).

**Test scenarios:**
- `useMcpConnections` hook: connect mutation invalidates the connections query.
- Connected Apps section renders the connection list and per-tool checkboxes.

**Verification:** hook test green; manual UI walkthrough: as Admin, connect GitHub, enable only a
read-only tool, confirm attempting a disabled tool fails visibly.

---

**Sprint 3 verification:** unit tests for `McpToolInvoker`'s enforcement, an integration test
connecting GitHub and invoking a commit-lookup tool end-to-end, manual UI walkthrough as above.

---

## Sprint 4 — Closed-Loop Feedback + Handoff Package

### U17. FeedbackEntry reason codes + retrieval down-weighting

**Goal:** Add reason codes to negative feedback; down-weight repeatedly-downvoted sources in
retrieval scoring.

**Requirements:** closed-loop feedback (PRD 5.7, "lightweight" framing — explicitly not full
re-indexing).

**Dependencies:** U5 (builds on `FeedbackEntry.UserId`), U10 (reuses `ProductVersion`/`Config`
persistence already added to query logging).

**Files:**
- `backend/SupportForge.Core/FeedbackEntry.cs` (modify) — add a small fixed reason-code enum
  (irrelevant/wrong-version/incomplete/other — refine with whoever reviews this plan), link to the
  sources/version already available on the query (reuse U10's persistence, don't duplicate).
- `backend/SupportForge.VectorStore/Chroma/ChromaVectorStoreService.cs` (modify) — a per-source
  penalty applied to distance results based on repeated negative feedback; scheduled or on-write
  signal, not full re-indexing.
- `backend/SupportForge.Agents/KbResearcherAgent.cs` (modify, if the penalty needs to be read at
  query time rather than baked into the store) — apply the penalty in retrieval scoring.
- `backend/SupportForge.Api.Tests/VectorStore/` (new/modify) — test for rank-drop over successive
  negative-feedback queries.

**Test scenarios:**
- A source receives repeated negative feedback across successive queries: its retrieval rank drops
  measurably compared to a baseline run.
- A source with no negative feedback: rank unaffected.
- Reason code is required on "not useful" feedback but optional/absent on "useful" feedback.

**Verification:** test confirming a repeatedly-downvoted source's retrieval rank drops over
successive queries; `dotnet test` green.

---

### U18. Admin feedback dashboard endpoint

**Goal:** Recent negative feedback + reason-code breakdown, scoped to the Admin's orgs/projects.

**Requirements:** feedback observability for Admins.

**Dependencies:** U17.

**Files:**
- `backend/SupportForge.Api/Controllers/FeedbackController.cs` (modify) — new endpoint (e.g.
  `GET api/feedback/dashboard`), `[Authorize(Roles="Admin")]`, scoped to orgs/projects the Admin
  belongs to.
- `backend/SupportForge.Api.Tests/Controllers/FeedbackControllerTests.cs` (modify).

**Test scenarios:**
- Admin sees negative feedback + reason-code breakdown only for projects in their own org.
- Non-Admin caller: 403.

**Verification:** `dotnet test` green.

---

### U19. Escalation entity + escalate/claim/queue/my-issues endpoints

**Goal:** L1 (and every role) can escalate a conversation; the handoff Markdown is assembled once at
escalation time from `AgentContext`'s already-accumulated state and cached — no pipeline re-run.

**Requirements:** closed-loop escalation (PRD 5.1's per-engineer dashboard acceptance criterion).

**Dependencies:** U6 (reuses the "pipeline computes full detail regardless of role" precedent), U4
(role gating on the queue/claim endpoints).

**Files:**
- `backend/SupportForge.Core/Escalation.cs` (new) — `ConversationId`, `EscalatedByUserId`,
  `EscalatedAt`, cached Markdown, `Status` (Open/Claimed/Resolved), `ClaimedByUserId?`.
- `backend/SupportForge.Core/IEscalationRepository.cs`, `JsonFileEscalationRepository.cs` (new) —
  mirror the existing entity/repo pairing convention.
- `backend/SupportForge.Api/Controllers/EscalationsController.cs` (new):
  - `POST /api/conversations/{id}/escalate` — available to **every role including L1** (L1 is the
    persona PRD-2.0 describes as needing "easy escalation"; gating it to L2/L3 would block the role
    that needs it most). Assembles the handoff Markdown once from `AgentContext`'s state (KB
    snippets, code findings, blast radius, vision findings, freshness, version/config) and caches it
    on the `Escalation` record. `CodeAnalyzerAgent`/`VisionAnalyzerAgent` already computed full code
    detail and blast radius when the original query ran, regardless of the asking user's role —
    role only gates what's rendered back to that user, not what's computed (same precedent as U6).
    Caching at escalation time means L2/L3 read the same detail L1's query already produced, instead
    of re-running the pipeline (re-spending LLM tokens).
  - `GET /api/escalations`, `[Authorize(Roles="L2,L3,Admin")]` — lists open escalations across
    projects the caller belongs to.
  - `POST /api/escalations/{id}/claim` — assigns the caller, flips status to Claimed.
  - `GET /api/engineers/me/issues` — currently-claimed + recently-resolved escalations for the
    caller, filterable.
- No ticketing API call for the escalation itself — "Copy rich Markdown from the cached escalation"
  only, per the scoping decision to keep 5.6's actual Jira/GitHub Issues integration deferred to the
  Hub, later.
- `backend/SupportForge.Api.Tests/Controllers/EscalationsControllerTests.cs` (new).

**Test scenarios:**
- L1 triggers escalation on a code-shaped conversation: cached Markdown contains full code
  findings/blast radius even though L1's own chat response never showed them.
- Escalating does not trigger a second agent-pipeline run — assert on call count to the LLM client
  during the escalate call (token-spend regression guard).
- L3 lists open escalations, opens one, sees the cached Markdown directly (no re-query).
- L3 claims an escalation: status flips to Claimed, `ClaimedByUserId` set, appears in
  `GET /api/engineers/me/issues`.
- L1 caller (not L2/L3/Admin) hits `GET /api/escalations`: 403.

**Verification:** test confirming an L1-triggered escalation's cached Markdown contains full code
findings/blast radius, a test confirming escalating doesn't trigger a second pipeline run,
`dotnet test` green.

---

### U20. Frontend: feedback reason UI + Escalate button

**Goal:** Reason-code select on negative feedback; an Escalate action visible to every role.

**Requirements:** feedback + escalation frontend surface.

**Dependencies:** U17, U19.

**Files:**
- `frontend/src/hooks/useSubmitFeedback.ts` (modify) — extend the useful/not-useful control with a
  reason-code select when marking "not useful."
- `frontend/src/components/MessageBubble.tsx` or wherever the feedback UI is currently wired
  (confirm) (modify) — reason-code select.
- Results view component (confirm exact file) (modify) — "Escalate" button, **visible to every
  role**, calling the new escalate endpoint and confirming the escalation was queued (not showing
  the code-bearing Markdown to an L1 caller — L1 triggers escalation but still never sees the code
  detail themselves, consistent with Sprint 1's gating).

**Test scenarios:**
- Marking "not useful" requires a reason-code selection before submit is enabled.
- Escalate button is visible and functional for an L1-authenticated session, but confirms queuing
  without exposing code detail.

**Verification:** component tests green; manual walkthrough per Sprint 4's overall verification.

---

### U21. Frontend: EscalationQueuePage + MyIssuesPage

**Goal:** L2/L3/Admin queue view; per-engineer claimed/resolved dashboard.

**Requirements:** PRD 5.1's per-engineer dashboard.

**Dependencies:** U19.

**Files:**
- `frontend/src/pages/EscalationQueuePage.tsx` (new, or a section on `DashboardPage.tsx` if it
  already serves this kind of aggregate view — `DashboardPage.tsx` exists on `master`; check its
  current content before adding a new page) — list of open escalations visible to L2/L3/Admin, each
  opening to show the cached Markdown/code-detail/blast-radius directly (no re-query). A "Claim"
  action per row.
- `frontend/src/pages/MyIssuesPage.tsx` (new, or a section on the same dashboard) — the calling
  engineer's claimed-open + recently-resolved escalations, with filters.
- `frontend/src/pages/DashboardPage.tsx` or `AdminPage.tsx` (modify) — feedback/staleness summary
  section reading U18's dashboard data endpoint.

**Test scenarios:**
- Queue page lists open escalations; claiming one removes it from the open list and adds it to
  My Issues.
- Non-L2/L3/Admin session cannot reach the queue page (client-side gate mirroring U19's server
  gate).

**Verification:** manual walkthrough: as L1, ask a code-shaped question, click Escalate; as L3, find
it in the Escalation Queue, open it, confirm the code detail and blast radius are already there with
no re-run; claim it, confirm it now appears in "My Issues."

---

**Sprint 4 verification:** test confirming a repeatedly-downvoted source's retrieval rank drops over
successive queries, a test confirming an L1-triggered escalation's cached Markdown contains full
code findings/blast radius, a test confirming escalating doesn't trigger a second agent-pipeline run
(token-spend regression guard). Manual walkthrough per U21.

---

## Sprint 5 — Cross-Repo Impact & Dependency Graph (5.4)

**Why bounded this way:** the existing graph infrastructure (Neo4j, `GraphImportJob`,
`GraphDbQueryTool`, `CodeGraphExtractor`) is real and reusable, but its "imports" edges are resolved
only against files *within the same repo being extracted* — there is no cross-repo edge type today,
and one real bug (confirmed during enrichment research) blocks even having correct multi-repo data
in one project's graph. This sprint fixes the bug, adds one new bounded edge type, and reuses the
existing traversal/query pattern — it does not build a new graph engine.

### U22. Fix node-ID collision bug (prerequisite)

**Goal:** `GraphImportJob`'s Cypher `MERGE` must not collide nodes from different repos that share a
relative path.

**Requirements:** prerequisite for any trustworthy cross-repo data.

**Dependencies:** none (bugfix, independent of Sprint 0-4).

**Files:**
- `backend/SupportForge.Ingestion/Graph/GraphImportJob.cs` (modify) — confirmed at line 56:
  `MERGE (n:GraphNode {id: node.id, projectId: $projectId})` has no `repo` in the merge key; node
  objects only carry `repo` as a `SET` property (line 62), not as part of node identity. Fix by
  either including `repo` in the merge key, or repo-qualifying `node.id` at extraction time (e.g.
  `{repo}::{relativePath}`).
- `backend/SupportForge.Ingestion/Graph/CodeGraphExtractor.cs` (modify, if repo-qualifying at
  extraction time is chosen over changing the merge key) — ID generation.
- `backend/SupportForge.Api.Tests/Ingestion/GraphImportJobTests.cs` (new/modify).

**Test scenarios:**
- Two repos in the same project, each with a file at the same relative path: after import, two
  distinct `GraphNode`s exist (not one collided node).
- Existing single-repo import behavior is unchanged (no regression for the common case).

**Verification:** unit test proving the ID-collision fix — two repos, same relative path, distinct
nodes survive import; `dotnet test` green.

---

### U23. Endpoint node type + calls_endpoint edge type

**Goal:** Detect ASP.NET Core HTTP endpoints and cross-repo references to them, as a new node/edge
type.

**Requirements:** 5.4 cross-repo blast radius, bounded to what's actually in this codebase's own
repos to start.

**Dependencies:** U22.

**Files:**
- `backend/SupportForge.Ingestion/Graph/CodeGraphExtractor.cs` (modify) — add an **endpoint** node
  type and a **calls_endpoint** edge type, regex-heuristic (consistent with the file's existing
  "not a substitute for a language server" doc comment, lines 6-15):
  - Endpoint detection: ASP.NET Core `[HttpGet]`/`[HttpPost]`/`[Route]` attributes, matching the
    supported-file-types list already present (csharp/typescript/javascript/python/java/go) — scope
    to what's actually in this codebase's own repos; extend to other frameworks only when a real
    target repo needs it.
  - Usage detection: an `HttpClient` call, a hardcoded route string match, or similar heuristic
    signal that one file references another repo's endpoint path.
- `backend/SupportForge.Api.Tests/Ingestion/CodeGraphExtractorTests.cs` (modify) — extend with
  endpoint-detection fixture cases.

**Patterns to follow:** existing per-language definition regex pattern in `CodeGraphExtractor.cs`
(e.g. `JsImportPattern` for the import-edge precedent).

**Test scenarios:**
- A file with `[HttpGet]`/`[Route]` attributes: endpoint node created with correct route path.
- A file in a different repo making an `HttpClient` call to that route: `calls_endpoint` edge
  created between the two.
- A file with no HTTP attributes: no spurious endpoint node created.

**Verification:** `dotnet test` green with new fixture cases.

---

### U24. BlastRadiusQueryTool + pipeline wiring

**Goal:** Given a changed file or endpoint, traverse `calls_endpoint` edges to find cross-repo blast
radius; surface it as a gated finding on relevant code queries.

**Requirements:** 5.4 — traversal is already `projectId`-scoped, not repo-scoped, so cross-repo
traversal within one project works structurally once U22 and U23 land.

**Dependencies:** U22, U23, U6 (reuses the L2/L3 code-detail gating precedent — this is exactly the
kind of detail L1 shouldn't see either).

**Files:**
- `backend/SupportForge.Agents/Tools/BlastRadiusQueryTool.cs` (new), implementing a new interface
  next to `ICodeGraphQueryTool` — or a new query mode on `GraphDbQueryTool` directly, whichever
  keeps the diff smaller once U23's edge type is in place.
- `backend/SupportForge.Agents/CoordinatorPipeline.cs`, `AgentContext.cs` (modify) — surface a
  "Blast radius" finding on relevant code queries, reusing U6's L2/L3 code-detail gating.
- `backend/SupportForge.Api.Tests/Ingestion/BlastRadiusQueryToolTests.cs` (new) — fixture repo pair
  with a real cross-repo endpoint call.

**Test scenarios:**
- Fixture repo pair with a real cross-repo endpoint call: `BlastRadiusQueryTool` finds it and
  reports "Repo A → used by Repo B."
- A changed file with no cross-repo callers: empty blast radius, no false positives.
- L1 caller never receives the blast-radius field in a chat response, even when the tool found a
  real match (same gating precedent as U6).

**Verification:** fixture repo pair test confirming `BlastRadiusQueryTool` finds a real cross-repo
call; a test confirming L1 never receives the blast-radius field; `dotnet test` green.

---

### U25. Frontend: Blast radius sub-section

**Goal:** Extend `CodeDetailPanel` with a structured blast-radius list.

**Requirements:** 5.4 frontend surface — PRD 5.4 explicitly allows a structured list; a graph
visualization is real added UI complexity for unclear payoff at this data quality/confidence level.

**Dependencies:** U24, U9 (extends `CodeDetailPanel.tsx` from Sprint 1).

**Files:**
- `frontend/src/components/CodeDetailPanel.tsx` (modify) — "Blast radius" sub-section: a structured
  list ("Repo A → used by Repo B, Repo C"), not a graph visualization.

**Test scenarios:**
- Blast-radius field present in response: renders the structured list.
- Field absent: no sub-section rendered.

**Verification:** component test green; manual walkthrough per Sprint 5's overall verification.

---

**Sprint 5 verification:** unit test proving the ID-collision fix, a fixture repo pair with a real
cross-repo endpoint call confirming `BlastRadiusQueryTool` finds it, a test confirming L1 never
receives the blast-radius field.

---

## Sprint 6 — Collaborative Support ↔ Engineering Workspace (5.8)

**Why bounded this way:** no real-time layer exists at all (confirmed: no SignalR reference
anywhere in the solution), so this is the one genuinely greenfield sprint — but it broadcasts state
(`Conversation`, `ChatMessage`) that already exists, rather than inventing a new session model.

### U26. ConversationHub + invite + scoped elevated visibility

**Goal:** SignalR hub broadcasting conversation state; an invite flow granting an invited engineer
temporary elevated visibility scoped to that one conversation.

**Requirements:** 5.8 collaborative workspace.

**Dependencies:** U6 (code-detail gating precedent — invited engineers get elevated visibility
scoped to one conversation, not a role change), U9 (`ConversationSidebar.tsx` marking).

**Files:**
- `backend/SupportForge.Api/SupportForge.Api.csproj` (modify) — add
  `Microsoft.AspNetCore.SignalR` (confirmed: already part of the ASP.NET Core shared framework this
  API already runs on — no new external dependency; confirmed no SignalR reference exists yet).
- `backend/SupportForge.Api/Hubs/ConversationHub.cs` (new) — `Hub`, one SignalR group per
  `Conversation.Id`. Broadcasts new messages, agent-reasoning/progress events (reuse whatever
  `frontend/src/hooks/useChatQueryStream.ts` implies the current streaming mechanism is — inspect it
  before choosing the broadcast payload shape, its exact transport (SSE vs. fetch-stream) wasn't
  confirmed during research), and presence join/leave.
- `backend/SupportForge.Api/Controllers/ConversationsController.cs` (or wherever conversation
  endpoints live — confirm) (modify) — `POST /api/conversations/{id}/invite`: adds an invited user
  (picked from the org's employee list via Sprint 1's registration data — no separate contact/email
  system) to the conversation's access list and SignalR group. Gate to L2/L3/Admin (an L1 inviting
  an engineer into their own conversation is the intended flow; the gate is about who *can be
  invited to see code-bearing detail*, not who can ask for help).
- `backend/SupportForge.Core/Conversation.cs` (modify) — add a participant/invite list field.
- `backend/SupportForge.Core/IConversationRepository.cs`,
  its `JsonFile` implementation (modify) — add methods for adding/reading participants (confirmed
  absent today).
- Effective permissions: the invited engineer sees the Sprint 1 code-detail panel and Sprint 5
  blast-radius panel regardless of their own project membership, for the session's duration, scoped
  strictly to that one conversation — audit-logged (who was invited, by whom, when), matching the
  audit-logging precedent from U7.
- `backend/SupportForge.Api.Tests/Hubs/ConversationHubTests.cs`,
  `Controllers/ConversationsControllerTests.cs` (new/modify).

**Approach:**
1. Read `frontend/src/hooks/useChatQueryStream.ts` first to confirm the current streaming transport
   before designing the hub's broadcast contract — reuse that transport's message shape rather than
   inventing a second one.
2. Session history: already persisted via `IConversationRepository`; no new storage needed beyond
   the participant/invite list on `Conversation`.

**Test scenarios:**
- Two connected clients in one conversation's SignalR group both receive a broadcast message.
- An invited engineer's temporary elevated visibility is scoped to the one conversation and doesn't
  leak to their normal project access elsewhere.
- A non-L2/L3/Admin caller attempting to invite: rejected.
- Invite is audit-logged with actor, target, conversation id, timestamp.

**Verification:** integration test for the SignalR hub (two connected clients in one conversation
group both receive a broadcast message), a test confirming an invited engineer's temporary elevated
visibility is scoped to the one conversation and doesn't leak elsewhere; `dotnet test` green.

---

### U27. Frontend: Invite Engineer + presence + handoff export extension

**Goal:** Invite flow UI, presence indicator, conversation-list marking, and extending Sprint 4's
handoff export with collaborative session data.

**Requirements:** 5.8 frontend surface.

**Dependencies:** U26.

**Files:**
- `frontend/src/pages/ChatPage.tsx` (modify) — "Invite Engineer" button (visible to L2/L3/Admin)
  opening a user picker (org employees) — calls the new invite endpoint.
- `frontend/src/hooks/usePresence.ts` (new) — wired to the SignalR hub connection.
- `frontend/src/components/PresenceIndicator.tsx` (new, small) — avatars/initials of who's currently
  viewing.
- `frontend/src/components/ConversationSidebar.tsx` (modify) — mark conversations with an active
  collaborative session.
- Sprint 4's handoff/escalation export (wherever U19's cached-Markdown assembly lives) (modify) —
  extend to include collaborative session participants and notes when present, reusing that same
  assembly step, not a new export path.

**Test scenarios:**
- Invite button visible only to L2/L3/Admin sessions.
- Presence indicator shows both participants when two clients join the same conversation.
- Sidebar marks a conversation with an active session.

**Verification:** manual walkthrough: as L3, invite another employee into a live conversation,
confirm both see the same thread and presence indicator in real time.

---

**Sprint 6 verification:** integration test for the SignalR hub, a test confirming scoped elevated
visibility doesn't leak, manual walkthrough as above.

---

## Cross-cutting verification (after Sprint 6)

- Full `dotnet test` + frontend test suite green.
- End-to-end manual pass covering the whole flow: sign up (new org+Admin) → register an L1 and an
  L3 employee → connect GitHub in the Integration Hub → ask a version-tagged question with a
  screenshot as each employee → confirm role-appropriate detail visibility (including blast radius)
  → submit feedback → escalate as L1, claim as L3, confirm the cached handoff Markdown is complete
  with no re-run → invite the L1 employee into the L3's conversation and confirm real-time shared
  view + temporary elevated detail scoped to that conversation only.
- Confirm no regression in the RAG reliability fixes already on `master` (re-run the table/image/
  diagram/PDF eval fixtures added by that work).
