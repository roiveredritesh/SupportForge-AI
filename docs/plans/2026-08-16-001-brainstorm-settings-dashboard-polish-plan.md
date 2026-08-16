---
artifact_contract: ce-unified-plan/v1
artifact_readiness: implementation-ready
product_contract_source: ce-brainstorm
execution: code
deepened: 2026-08-16
---

# Sprint 7: Settings Restructure, Dashboard, and Polish - Plan

## Goal Capsule

**Objective:** Address 8 UX/product gaps surfaced during live E2E testing of the merged PRD-2.0
re-scope (`docs/plans/2026-08-15-001-feat-prd2-rescope-plan.md`, all 7 sprints on `master`):
richer org signup, a Settings section split by concern, a metrics-driven Dashboard, role-based
help, a proactive Connected-App token-expiry alert, grid layouts for Employees/Projects,
self-service password change, and a Re-index/Force-Reindex clarifying tooltip.

**Product authority:** This session (user-directed, following live testing of the running app).

**Open blockers:** None — all scope decisions below were confirmed interactively before writing
this plan.

## Context

SupportForge AI's org, RBAC, Integration Hub, and escalation features (Sprints 0-6) are complete
and merged. During a live Chrome walkthrough of the running app, the user identified 8 rough edges
that are real product/UX gaps, not bugs — distinct from the 5 defects found and fixed during that
same testing pass (role-claim mismatch, missing signup page, `Project.OrgId` not stamped, and two
Invite Engineer access bugs, all already merged).

## Deferred / Explicitly Out of Scope for This Plan

- **Admin-side password reset for employees** — only self-service password change (a user changing
  their own password) is in scope; an Admin "reset this employee's password" action is not built
  here.
- **List/grid toggle for Employees and Projects** — grid fully replaces the list layout; no toggle
  to switch back to a list view.
- **Interactive onboarding tours** — the role-based help section is a static content page, not an
  in-app guided walkthrough with tooltips/spotlights over live UI elements.
- **Fine-grained-PAT (and other MCP server) expiry detection** — the token-expiry alert only works
  for classic GitHub PATs, which report an expiration date via response header at connect-time.
  Fine-grained PATs and any future non-GitHub MCP server that doesn't report an expiry the same way
  get no proactive alert; a failure still surfaces reactively (the underlying tool call errors) the
  same way it does today.

## Product Contract

### Requirements

- **R1** — Org self-service registration (`/register`) collects `orgName` (required, existing),
  `contactPerson` (required, new), `contactNumber` (required, new), `industry` (required, new), and
  `address` (optional, new).
- **R2** — Settings (currently the single `/admin` route) splits into separate routed sub-pages:
  Projects, Employees, Connected Apps, Feedback — each reachable from a Settings sub-nav, mirroring
  the existing sidebar-nav pattern (Escalations/My Issues are already separate routes).
- **R3** — The Dashboard (currently just a project picker) is redesigned to show, for the selected
  project: query volume over time, feedback ratio (useful vs. not-useful), escalation stats
  (open/claimed/resolved counts), and token usage/cost (chat vs. ingestion breakdown) — all as
  charts/visualizations, not raw tables.
- **R4** — A new `/help` page shows role-varying static content: what each of L1/L2/L3/Admin can do
  in the app and the basic steps to do it (ask a question, escalate, claim an escalation, register
  an employee, connect an MCP server, etc. — scoped to what's relevant per role).
- **R5** — When a Connected App's token nears/reaches expiry, an alert surfaces to Admins (the role
  that manages Connected Apps). Expiry is captured from GitHub's PAT-expiration response header at
  connect-time; the connection stores an `ExpiresAt` value used to decide when to alert.
- **R6** — Employees and Projects render as a card grid (replacing the current list layout) in their
  respective Settings pages.
- **R7** — Users get a "Change Password" action in their own account area, self-service only (no
  Admin-reset-for-others in this plan).
- **R8** — The Re-index and Force Reindex buttons on the Projects settings page carry a tooltip or
  inline note clarifying the difference (Force Reindex clears the sync cache first; existing button
  `title` text already explains this partially — make it more visible, e.g. also on Re-index).

### Actors

- **Admin** — manages org settings, employees, connected apps; primary audience for R2, R5, R7
  (their own password), and sees all of R4's content plus Admin-specific steps.
- **L1/L2/L3 (Employee)** — ask questions, escalate (L1); claim/resolve escalations (L2/L3); all see
  R1 is not directly relevant to them (Admins register orgs), but see R3, R4 (their own role's
  slice), R6, R7, R8.

### Key Flows

- **F1 — Org signup with full details:** a new user visits `/register`, fills org name + the three
  new required fields (+ optional address), submits, and becomes that org's Admin — same
  auto-org-creation flow as today, extended with more fields on `Org`.
- **F2 — Navigating Settings:** an Admin clicks "Settings" in the sidebar, lands on a Settings
  landing/sub-nav, and picks Projects / Employees / Connected Apps / Feedback — each a distinct page
  instead of one long scroll.
- **F3 — Reading the Dashboard:** any authenticated user with a project selected sees that project's
  activity at a glance — query volume trend, feedback health, escalation backlog, token spend —
  without navigating elsewhere.
- **F4 — Token expiry alert:** an Admin connects GitHub with a classic PAT that has an expiration
  date; as that date approaches, the Admin sees an alert (e.g. on the Dashboard or Connected Apps
  page) prompting them to reconnect with a fresh token before it lapses.
- **F5 — Self-service password change:** a logged-in user finds "Change Password" in their account
  area, enters their current and new password, and it takes effect — matching how login works today
  (no email/reset-link infrastructure, this is an authenticated in-session change).

### Acceptance Examples

- **AE1 (covers F1).** A user submits `/register` with org name "Acme Support", contact person
  "Jane Doe", contact number "+1-555-0100", industry "Healthcare", no address. Signup succeeds; the
  org record stores all four submitted fields plus a null address.
- **AE2 (covers F1).** A user submits `/register` missing `contactNumber`. Signup is rejected with a
  validation message naming the missing required field.
- **AE3 (covers F2).** An Admin navigates to Settings > Employees. The URL is a distinct route (not
  `/admin`); the page shows only the Employees content, not Projects/Connected Apps/Feedback.
- **AE4 (covers F3).** An L1 user with "SupportForge Demo" selected on the Dashboard sees a chart of
  their project's query volume for the last N days, without navigating to a separate analytics page.
- **AE5 (covers F4).** An Admin connects GitHub with a PAT that expires in 5 days. Within that
  window, an alert becomes visible stating the connection will expire soon. After it actually
  expires, the alert's wording reflects that it has expired (not just "will expire").
- **AE6 (covers R6).** Settings > Employees renders each employee as a card (name, role, project
  scope) in a grid, not a vertical list.
- **AE7 (covers F5).** A logged-in user changes their password via the account area, providing their
  correct current password and a new one; they can then log in with the new password and the old
  one no longer works.
- **AE8 (covers R8).** Hovering or reading near the Re-index and Force Reindex buttons shows text
  distinguishing them (Force Reindex clears the sync cache and re-processes unchanged content;
  Re-index does not).

### Scope Boundaries

**In scope:** the 8 items as scoped above (R1-R8).

**Deferred for later:** see "Deferred / Explicitly Out of Scope for This Plan" above (admin-side
password reset, list/grid toggle, interactive onboarding tours, fine-grained-PAT expiry detection).
Additionally: org-wide (cross-project) aggregate dashboard view — R3's metrics are scoped to the
currently selected project, matching the existing project-scoped pattern used elsewhere in the app.

**Outside this product's identity:** none identified — all 8 items fit the existing internal
support-tool shape confirmed in the PRD-2.0 re-scope.

### Key Technical Decisions

- **KTD1** (session-settled: user-directed — chosen over "all fields optional" or "all fields
  required": balances data completeness against signup friction) — `contactPerson`,
  `contactNumber`, and `industry` are required on org registration; `address` stays optional.
- **KTD2** (session-settled: user-directed — chosen over a single-page-with-tabs alternative) —
  Settings splits into separate routed sub-pages (Projects, Employees, Connected Apps, Feedback),
  not tabs within one page.
- **KTD3** (session-settled: user-directed) — Dashboard shows all four proposed metrics (query
  volume, feedback ratio, escalation stats, token usage/cost), not a subset.
- **KTD4** (session-settled: user-directed — chosen over an interactive guided tour) — Help is a
  static per-role content page (`/help`), not an interactive walkthrough.
- **KTD5** (session-settled: user-directed — chosen over a reactive "alert only after a failed
  call" approach) — token expiry is captured proactively from GitHub's PAT-expiration response
  header at connect-time, accepting the known limitation that fine-grained PATs and other MCP
  servers without that header won't get proactive alerts.
- **KTD6** (session-settled: user-directed — chosen over a list/grid toggle) — grid layout fully
  replaces the list for Employees and Projects.
- **KTD7** (session-settled: user-directed — chosen over adding Admin-reset for employees too) —
  change password is self-service only in this plan.

## Outstanding Questions

- Whether "industry" should eventually become a fixed dropdown (vs. free text) once real usage
  patterns emerge — free text is the starting assumption; revisit if data quality becomes an issue.

---

## Planning Contract

**Product Contract preservation:** unchanged from the brainstorm — no restructuring, no scope
change. The two Outstanding Questions from the requirements pass are resolved below as planning
decisions (KTD8, KTD9), since both were planning-time, not product-time, questions.

- **KTD8** — Charts (R3) use `recharts`, a new frontend dependency (~90kb gzipped, React-native
  API, no imperative canvas code to hand-roll). Chosen over hand-rolled inline SVG: four distinct
  chart types (line, bar breakdowns) hand-rolled would cost more ongoing maintenance than one small,
  widely-used dependency, and the user explicitly asked for a "more professional" look.
- **KTD9** — The token-expiry alert (R5/F4) renders on the Dashboard (`/`, the app's home page),
  visible to Admin only — matches the user's original phrasing ("alert on the home page") and the
  Dashboard is already the landing route.

**Repo/project map** (only paths new to this plan; see `docs/plans/2026-08-15-001-feat-prd2-rescope-plan.md`
for the full backend/frontend map, still accurate): `backend/SupportForge.Api/Controllers/` for new
endpoints, `frontend/src/pages/` for new routed pages, `frontend/src/components/` for extracted
section components (existing `EmployeesSection.tsx`/`ConnectedAppsSection.tsx`/
`FeedbackDashboardSection.tsx` already live there and are the direct precedent for R2's split).

---

## Verification Contract

- `dotnet test` (all backend test projects) and the frontend test suite green after every unit.
- Each unit's own manual walkthrough (below) passes before its PR.
- Cross-cutting end-to-end walkthrough (below) passes once all units land.

## Definition of Done

- All 10 Implementation Units complete, `dotnet test` and the frontend suite green on `master`.
- The cross-cutting manual pass (below) completes successfully.
- No regression in existing RBAC/escalation/Integration Hub behavior (Settings split must not change
  any endpoint's authorization; it only reorganizes navigation).

---

## Implementation Units

### U1. Org entity + registration request fields (backend)

**Goal:** Extend `Org` with the new contact/industry/address fields and give `/api/auth/register`
its own request shape distinct from login's `TokenRequest`.

**Requirements:** R1, KTD1.

**Dependencies:** none.

**Files:**
- `backend/SupportForge.Core/Entities/Org.cs` (modify) — add `ContactPerson` (required, non-null
  string), `ContactNumber` (required), `Industry` (required), `Address` (optional, `string?`). `Name`
  stays required (already is — this is `orgName` from R1).
- `backend/SupportForge.Api/Controllers/AuthController.cs` (modify) — add a new
  `RegisterRequest(string OrgName, string UserName, string Password, string ContactPerson, string
  ContactNumber, string Industry, string? Address)` record (do not reuse `TokenRequest`, which stays
  login-only). `Register` action: validate the four required fields are non-empty (return
  `BadRequest` naming the missing field(s) — mirror the existing `Conflict`/`BadRequest` pattern
  already in this action), use `request.OrgName` instead of the current auto-generated
  `"{username}'s Org"` name, and set the new `Org` fields from the request.

**Approach:**
1. Model binding validation: ASP.NET Core's `[Required]` data annotations on the record properties
   is the simplest fit, consistent with this controller's existing manual-check style — either
   annotations + `ModelState.IsValid` check, or explicit `string.IsNullOrWhiteSpace` checks matching
   the existing `Conflict("Username is already taken.")` line's style. Follow whichever pattern is
   already dominant in `AuthController`/`OrgsController` once read at implementation time.
2. `Org.Name` continues to be set from `request.OrgName` (was `$"{request.UserName}'s Org"` —
   replace, don't duplicate).

**Patterns to follow:** `backend/SupportForge.Api/Controllers/OrgsController.cs`'s
`RegisterEmployeeRequest` record for the "distinct request record per action" convention already
used in this codebase.

**Test scenarios:**
- Register with all fields (including optional address) populated: org created with all five
  fields stored correctly.
- Register with address omitted: org created, `Address` is null.
- Register missing `contactPerson`/`contactNumber`/`industry` (each individually): `BadRequest`
  naming the missing field, no user or org created.
- Register with a duplicate username (existing behavior): still returns `Conflict` before touching
  org creation — confirm the new fields don't change this ordering.

**Verification:** `dotnet test` green; the existing `AuthControllerTests.cs` register test(s)
updated for the new request shape.

---

### U2. Org registration form fields (frontend)

**Goal:** Add the four new fields to `RegisterPage.tsx`.

**Requirements:** R1, AE1, AE2.

**Dependencies:** U1.

**Files:**
- `frontend/src/pages/RegisterPage.tsx` (modify) — add "Organization Name" (required — currently
  missing entirely, since org name is auto-generated today), "Contact Person" (required), "Contact
  Number" (required), "Industry" (required), "Address" (optional) fields, matching the existing
  input styling already in this file.
- `frontend/src/hooks/useRegister.ts` (modify) — extend the request payload to match U1's
  `RegisterRequest` shape.

**Test scenarios:**
- Submitting with all required fields filled calls the register endpoint with the full payload.
- Submitting with a required field empty shows a validation error and does not call the endpoint
  (client-side check) — or, if the form relies on server validation, displays the server's error
  message from a failed request (match whichever pattern `LoginPage.tsx`/`RegisterPage.tsx` already
  use for error display).

**Verification:** component/hook tests green; manual walkthrough: register a new org with all
fields, confirm success; omit a required field, confirm a clear error.

---

### U3. Settings section split into routed sub-pages (frontend)

**Goal:** Replace the single `/admin` page with a Settings section containing separate routes for
Projects, Employees, Connected Apps, and Feedback.

**Requirements:** R2, KTD2, AE3.

**Dependencies:** none (independent of U1/U2).

**Files:**
- `frontend/src/App.tsx` (modify) — replace the single `<Route path="/admin" element={<AdminPage
  />} />` with routes under `/settings`: `/settings/projects`, `/settings/employees`,
  `/settings/connected-apps`, `/settings/feedback` (exact path segments — confirm no collision with
  existing routes). Keep `/admin` as a redirect to `/settings/projects` if any existing links/tests
  depend on the old path, or update all internal links (see below) — implementer's call based on
  what's cleaner once the full link surface is visible.
- `frontend/src/pages/AdminPage.tsx` (modify/split) — the current file already composes
  `EmployeesSection`, `ConnectedAppsSection`, `FeedbackDashboardSection` as separate components
  (Sprint 1/3/4 precedent) plus inline project-management JSX. Extract the inline project-management
  form/list into its own `SettingsProjectsPage.tsx` (or similar), and create thin page components
  `SettingsEmployeesPage.tsx`, `SettingsConnectedAppsPage.tsx`, `SettingsFeedbackPage.tsx` that each
  render one existing section component. `AdminPage.tsx` itself is removed once all four pieces have
  homes.
- `frontend/src/components/Layout.tsx` (modify) — the "Settings" nav link currently points to
  `/admin`; either point it at a new Settings landing page with sub-nav, or expand it into a
  sub-menu — follow whatever pattern this file already uses for nav items (it's the file that
  currently defines the `roles`-gated nav array per Sprint 4/6 comments).
- New: a small Settings sub-nav component (e.g. `SettingsNav.tsx`) shared across the four settings
  pages, showing the four section links with the active one highlighted — mirrors the top-level
  sidebar's own active-link styling.

**Approach:**
1. This is a pure reorganization — no backend change, no new authorization logic. Every section's
   existing `RequireRole` gate (Employees/Connected Apps already Admin-gated client-side) moves with
   it unchanged.
2. Read `AdminPage.tsx`'s full current content before splitting — it may have shared state (e.g. the
   selected-project context) that needs to move to a shared layout wrapper rather than being
   duplicated per page.

**Patterns to follow:** `frontend/src/components/EmployeesSection.tsx`,
`ConnectedAppsSection.tsx`, `FeedbackDashboardSection.tsx` (already-extracted precedent);
`frontend/src/App.tsx`'s existing `/escalations`/`/my-issues` route definitions for the routed-page
convention.

**Test scenarios:**
- Navigating to `/settings/employees` renders only the Employees content (no Projects/Connected
  Apps/Feedback).
- Navigating to `/settings/connected-apps` as a non-Admin: existing client-side `RequireRole` gate
  still hides Admin-only content (server-side enforcement is unchanged, out of scope here).
- The Settings sub-nav highlights the currently active section.
Test expectation for the pure route-registration change in `App.tsx`: covered by the per-page
render tests above, not a separate scenario.

**Verification:** component tests for each new page green; manual walkthrough: as Admin, visit each
of the four Settings sub-pages, confirm each shows only its own content and the sub-nav reflects the
active page.

---

### U4. Dashboard metrics data endpoints (backend)

**Goal:** Expose the four metrics R3 needs as project-scoped endpoints, reusing existing storage.

**Requirements:** R3, KTD3.

**Dependencies:** none.

**Files:**
- `backend/SupportForge.Api/Controllers/ProjectsController.cs` (modify) — extend or add alongside
  the existing `GetTokenUsage` (`{id}/token-usage`) action:
  - `GET {id}/query-volume` — buckets `ITokenUsageRepository` entries with `Source == "chat"` by day
    (reusing `TokenUsageEntry.CreatedAt`, already present per-entry) over a fixed recent window (e.g.
    last 30 days — confirm against what `ITokenUsageRepository`'s query methods already support;
    extend the interface if it only exposes aggregate sums today).
  - `GET {id}/feedback-summary` — useful vs. not-useful counts for the project, reusing
    `IFeedbackRepository` (the existing org-wide `/api/feedback/dashboard` endpoint in
    `FeedbackController.cs` is Admin-only and org-scoped; this is a new project-scoped, role-open
    endpoint any project member can call — do not reuse the Admin-gated one).
  - `GET {id}/escalation-stats` — open/claimed/resolved counts for the project, reusing
    `IEscalationRepository` (check its current query surface — `GetAllAsync` plus in-memory
    filtering by `ProjectId`/`Status` is acceptable at this data scale, matching
    `EscalationsController.Queue`'s existing in-memory-filter pattern).
  - `token-usage` (existing) already covers the fourth metric — no change needed there beyond
    confirming its response shape (chat vs. ingestion breakdown) is chart-ready.
- `backend/SupportForge.Core/ITokenUsageRepository.cs` (modify, only if needed) — add a
  day-bucketed query method if the existing interface can't already answer "entries per day for
  this project".

**Approach:**
1. All four endpoints follow `ProjectsController`'s existing pattern: `[Authorize]`,
   `IsMemberAsync` check, no role restriction beyond project membership (every role sees the
   Dashboard, per F3).
2. Prefer extending existing repositories over adding new ones — this data already exists.

**Test scenarios:**
- Query volume: seed `TokenUsageEntry` rows across 3 different days for a project, confirm the
  endpoint returns per-day counts matching the seeded data.
- Feedback summary: seed useful/not-useful feedback entries, confirm counts match.
- Escalation stats: seed escalations in Open/Claimed/Resolved states for the project, confirm counts
  match and escalations from a different project are excluded.
- Non-member caller for any of the three new endpoints: `Forbid`.

**Verification:** `dotnet test` green; each endpoint manually confirmed against seeded data via the
running API.

---

### U5. Dashboard redesign with charts (frontend)

**Goal:** Replace the current minimal Dashboard content with the four metric charts.

**Requirements:** R3, KTD3, KTD8, AE4.

**Dependencies:** U4.

**Files:**
- `frontend/package.json` (modify) — add `recharts` (KTD8).
- `frontend/src/pages/DashboardPage.tsx` (modify) — keep the existing project selector (it's still
  needed), replace the "New Query"/"Settings" button row's role as the page's only content with: a
  line chart (query volume over time), a bar or donut chart (feedback ratio), a small stat/bar
  breakdown (escalation stats), and a stacked bar or breakdown (token usage chat vs. ingestion). Keep
  the "New Query" quick-action, drop the "Settings" link (Settings is now reached via the sidebar
  per U3, not a Dashboard button).
- New hooks: `frontend/src/hooks/useQueryVolume.ts`, `useFeedbackSummary.ts`,
  `useEscalationStats.ts` (mirror `useProjects.ts`'s `useQuery` shape) calling U4's endpoints.
  `useTokenUsage` may already exist for the existing `token-usage` endpoint — check before adding a
  duplicate.
- New: `frontend/src/components/TokenExpiryAlert.tsx` (Admin-only, R5 — see U7; wired into
  `DashboardPage.tsx` here since this unit owns the Dashboard layout).

**Approach:**
1. Charts render only when a project is selected (same gate the current page selector already has).
2. No project selected: keep today's "select a project" empty state, don't attempt to render empty
   charts.

**Patterns to follow:** `frontend/src/hooks/useProjects.ts` for hook shape;
`frontend/src/components/ConfidenceBadge.tsx`/`CodeDetailPanel.tsx` for the existing card/panel
Tailwind styling to match.

**Test scenarios:**
- Dashboard with a project selected and data present: all four chart sections render.
- Dashboard with no project selected: shows the existing empty-state, no chart attempts, no
  endpoint calls fired.
- Dashboard with a project selected but zero data (new project): charts render an empty/zero state,
  not an error.

**Verification:** component tests green; manual walkthrough: select a project with real activity,
confirm all four charts render with correct-looking data; select a brand-new empty project, confirm
no errors.

---

### U6. Role-based static help page

**Goal:** A new `/help` page with role-varying static content.

**Requirements:** R4, KTD4.

**Dependencies:** none.

**Files:**
- `frontend/src/pages/HelpPage.tsx` (new) — static content sectioned by role (L1: ask questions,
  give feedback, escalate; L2/L3: claim/resolve escalations, invite engineers, see code detail;
  Admin: all of the above plus register employees, connect apps, manage projects). Render only the
  sections relevant to the logged-in user's role (reuse `useAuthStore`'s `role`, same pattern
  `RequireRole`/`ChatPage.tsx`'s `ELEVATED_ROLES` already use), or show all sections with the
  user's own role's section expanded/highlighted — implementer's call on which reads better; content
  is static prose either way.
- `frontend/src/App.tsx` (modify) — add `<Route path="/help" element={<HelpPage />} />` inside the
  `RequireAuth` wrapper (help content assumes a logged-in role).
- `frontend/src/components/Layout.tsx` (modify) — add a "Help" nav link, visible to every role (no
  `RequireRole` gate — everyone gets help).

**Test scenarios:**
- An L1 session sees L1-relevant content and does not see Admin-only steps (e.g. "register an
  employee") presented as something they can do.
- An Admin session sees the full content set.
Test expectation: this is a content-scoping test on static prose, not a coverage-heavy unit — one
test per role tier confirming role-appropriate sections render is sufficient.

**Verification:** component tests green; manual walkthrough as L1 and as Admin, confirming the
content differs appropriately.

---

### U7. Connected App token-expiry capture and alert

**Goal:** Capture GitHub PAT expiry at connect-time and alert Admins as it approaches/passes.

**Requirements:** R5, KTD5, KTD9, AE5.

**Dependencies:** none (independent; U5 wires the alert component into the Dashboard, but this unit
can land the backend + component first).

**Files:**
- `backend/SupportForge.Core/Entities/McpConnection.cs` (modify) — add `ExpiresAt` (`DateTimeOffset?`
  — null for connections where no expiry was reported, e.g. fine-grained PATs, per the plan's
  documented limitation).
- `backend/SupportForge.Api/Controllers/McpConnectionsController.cs` (modify) — in `Connect`, when
  `ServerType == "github"`, make one lightweight authenticated GitHub API call (e.g. `GET
  https://api.github.com/user` — read-only, minimal scope needed) using the submitted credential,
  read the `github-authentication-token-expiration` response header if present, parse it into
  `ExpiresAt`. If the header is absent (fine-grained PAT) or the call fails (bad token), still allow
  the connection to be saved (bad-token detection isn't this unit's job — the existing
  `McpToolInvoker` surfaces bad-credential failures at actual tool-use time) but leave `ExpiresAt`
  null.
- `GET {orgId}/mcp-connections` (existing, in the same controller) — include `ExpiresAt` in
  `ConnectionSummary` (it's not a credential, safe to return).
- New: `GET api/orgs/{orgId}/mcp-connections/expiring` (or fold into the existing list response) —
  used by the frontend alert to check for connections expiring within a threshold (e.g. 7 days) or
  already expired. A dedicated endpoint keeps `TokenExpiryAlert.tsx` simple; folding into the
  existing list also works — implementer's call, but document the choice in a one-line comment.

**Approach:**
1. Use `HttpClient` for the GitHub probe call — same pattern `CommitLookupTool.cs`'s GitHub REST
   usage already established (Sprint 2), including its graceful-degradation-on-failure precedent.
2. Threshold for "nearing expiry" (e.g. 7 days) is a constant, not configurable in this plan —
   simplest thing that satisfies AE5's "within that window" requirement.

**Patterns to follow:** `backend/SupportForge.Agents/Tools/CommitLookupTool.cs` for the GitHub REST
`HttpClient` call and graceful-degradation pattern.

**Test scenarios:**
- Connecting with a token whose response includes an expiration header: `ExpiresAt` is parsed and
  stored correctly.
- Connecting with a token whose response has no expiration header (fine-grained PAT): connection
  saves successfully, `ExpiresAt` is null, no error.
- Connecting when the GitHub probe call itself fails (network/bad token): connection still saves
  (degrades gracefully, per KTD5's documented limitation), `ExpiresAt` null.
- A connection with `ExpiresAt` 3 days out: appears in the "expiring soon" check.
- A connection with `ExpiresAt` in the past: appears as "expired," not "expiring soon" — the
  frontend alert wording differs (AE5).
- A connection with `ExpiresAt` 60 days out: does not appear in the "expiring soon" check.

**Verification:** `dotnet test` green including the graceful-degradation case; manual walkthrough
connecting a real classic PAT (if available) or a stubbed/faked one in a test environment, confirming
`ExpiresAt` populates.

---

### U8. Employees and Projects grid layout

**Goal:** Replace list rendering with a card grid for both Employees and Projects.

**Requirements:** R6, KTD6, AE6.

**Dependencies:** U3 (these live in `SettingsEmployeesPage.tsx`/`SettingsProjectsPage.tsx` after the
split).

**Files:**
- `frontend/src/components/EmployeesSection.tsx` (modify) — change the employee list `<ul>`/`<li>`
  rendering to a CSS grid of cards (Tailwind `grid grid-cols-*` — match the responsive breakpoint
  pattern already used elsewhere, e.g. `ConnectedAppsSection.tsx` or `MessageThread.tsx`, if either
  already uses a grid).
- `frontend/src/pages/SettingsProjectsPage.tsx` (the U3 extraction target for the current
  `AdminPage.tsx` project list) (modify) — same grid treatment for the Existing Projects list.

**Test scenarios:**
- Employees list with 3 employees renders 3 cards in a grid container (assert on the grid container
  class/structure, not exact CSS values).
- Projects list with 2 projects renders 2 cards in a grid container.
- Zero employees / zero projects: existing empty-state text still renders (no broken empty grid).

**Verification:** component tests green; manual visual check at desktop and mobile widths (per this
project's responsive-review convention) confirming the grid reflows sensibly on a narrow viewport.

---

### U9. Self-service change password

**Goal:** Let a logged-in user change their own password.

**Requirements:** R7, KTD7, F5, AE7.

**Dependencies:** none.

**Files:**
- `backend/SupportForge.Api/Controllers/AuthController.cs` (modify) — add `POST
  api/auth/change-password`, `[Authorize]` (any authenticated user, no role restriction), request
  `ChangePasswordRequest(string CurrentPassword, string NewPassword)`. Use
  `UserManager<AppUser>.ChangePasswordAsync` (ASP.NET Core Identity's built-in method — verifies the
  current password and applies the new one's validation rules in one call, no need to hand-roll
  verification).
- Frontend: a new `frontend/src/pages/AccountPage.tsx` (or a section within an existing account
  area if one already exists — check `Layout.tsx`/`App.tsx` for any existing
  account/profile route before creating a new one) with a change-password form.
- `frontend/src/hooks/useChangePassword.ts` (new) — mirrors `useLogin.ts`'s `useMutation` shape.
- `frontend/src/components/Layout.tsx` (modify) — add an "Account" nav link (or a link from the
  existing user-menu/log-out area, if one exists) to the new page.

**Test scenarios:**
- Correct current password + valid new password: succeeds, `ChangePasswordAsync` called with both
  values.
- Wrong current password: fails with a clear error (Identity's own `PasswordMismatch` error surfaces
  through the same `result.Errors` pattern `AuthController.Register` already uses).
- New password fails Identity's password-strength rules (existing validators, unchanged): fails with
  that validator's error message.
- Covers AE7: after a successful change, logging in with the new password succeeds and the old
  password is rejected (`Token` action against the same user).

**Verification:** `dotnet test` green; manual walkthrough: change password, log out, log back in
with the new password (confirm old one is rejected).

---

### U10. Re-index / Force Reindex clarifying note

**Goal:** Make the distinction between the two buttons visible without hovering-only discovery.

**Requirements:** R8, AE8.

**Dependencies:** U3 (lives in `SettingsProjectsPage.tsx` after the split — if U3 hasn't landed yet
when this unit runs, apply to `AdminPage.tsx` directly and let U3's extraction carry it forward).

**Files:**
- `frontend/src/pages/SettingsProjectsPage.tsx` (or `AdminPage.tsx` if this lands first) (modify) —
  `Force Reindex`'s button already has a `title` attribute explaining the difference (confirmed:
  "Clears the sync cache first, so unchanged sources re-chunk too -- use after an ingestion logic
  update"). Add a matching, brief inline note (not just a tooltip — AE8 asks for something visible
  without hovering) near both buttons, or add a `title` to `Re-index` too for symmetry, plus a small
  static caption under the button row. Simplest fix: one line of caption text under the two buttons,
  e.g. "Re-index picks up new/changed content. Force Reindex also re-processes unchanged content —
  use after an ingestion logic update."

**Test scenarios:**
Test expectation: none — this is a static caption/copy change with no behavioral logic to test.
(Feature-bearing exception per plan-quality convention: pure copy/UI text needs no test.)

**Verification:** manual visual check — the distinction is readable on the page without hovering
either button.

---

## Cross-cutting verification (after all units land)

- Full `dotnet test` + frontend test suite green.
- End-to-end manual pass: register a new org with all required fields → land on Dashboard, see
  empty-state charts for the new project → create a project, ask a few questions, submit feedback,
  escalate → return to Dashboard, confirm all four charts now show data → visit each Settings
  sub-page, confirm Employees/Projects render as grids → connect GitHub with a token, confirm the
  Connected Apps list shows it (and, if using a classic PAT with a known near-term expiry, confirm
  the Dashboard alert appears) → visit `/help`, confirm role-appropriate content → change password,
  log out, log back in with the new password → confirm Re-index/Force Reindex distinction is visible
  on the Projects settings page.
- Confirm no regression in RBAC/escalation/Integration Hub behavior — the Settings split must not
  have changed any endpoint's authorization, only frontend routing.
