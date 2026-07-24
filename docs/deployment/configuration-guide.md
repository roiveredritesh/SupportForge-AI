# SupportForge AI — Configuration Guide

## Required configuration (`appsettings.Production.json` or environment variables)

| Key | Purpose | Example |
|---|---|---|
| `OpenAI:ApiKey` | LLM completions, embeddings, vision | `sk-...` |
| `VectorStore:Provider` | `Chroma` (local) or `Pinecone` (prod) | `Pinecone` |
| `VectorStore:Chroma:BaseUrl` | Chroma server URL (local only) | `http://localhost:8000` |
| `VectorStore:Pinecone:ApiKey` | Pinecone API key (prod only) | `...` |
| `VectorStore:Pinecone:Environment` | Pinecone environment/region | `us-east-1` |

As environment variables, replace `:` with `__` (double underscore) — e.g. `OpenAI:ApiKey` becomes `OpenAI__ApiKey`. This is .NET's configuration provider convention; setting `OpenAI:ApiKey` as a literal env var name will not bind.

## Onboarding a new project
1. Open the Admin page (`/admin`).
2. Enter a unique Project ID (lowercase, no spaces — used directly as a vector collection prefix) and a display Name.
3. Optionally add one GitHub repo (owner + repo name; the app clones the default branch over HTTPS — for private repos, an access token must be added to `GitHubRepoConfig.AccessTokenSecretName` and the corresponding secret provisioned; this is a known MVP gap, see below).
4. Optionally add a KB Documents folder path (must be reachable from the backend host's filesystem, e.g. a mounted network share).
5. Click "Create Project", then "Re-index" to trigger ingestion.
6. Check `GET /api/projects/{id}/freshness` (or the Dashboard, once wired to it) to confirm ingestion completed.

## Known MVP gaps (explicitly out of scope per TSD §7 risk mitigation)
- Private GitHub repo auth (`AccessTokenSecretName`) is modeled but not wired to a secret store — add before onboarding any private repo.
- Entra ID auth/role-based access (PRD §7) is not implemented in this 7-day plan — the API and UI are unauthenticated. This must land before any external-facing deployment.
- Only one KB connector (local Documents) ships; Confluence is deferred to Phase 2 per PRD §9 roadmap.
- Analytics/Monitoring screen (PRD §5d) is limited to the token-usage counter from Task 20 — full dashboard is Phase 2.

## Known deviations from the original plan
- **.NET 9 instead of .NET 8**: the plan's Tech Stack section specifies .NET 8, but only the .NET 9 SDK was available in the build environment, so all projects target `net9.0`. This was a deliberate, human-approved decision. If .NET 8 is later required (e.g. for a specific hosting constraint), retargeting should be low-risk since no .NET 8-specific APIs are used.
- **Dark/light mode toggle**: not covered by any single task in the original 24-task plan (a gap discovered during Task 19's review); added as a small follow-up fix wiring `useAppStore`'s existing `theme`/`toggleTheme` state to a visible `ThemeToggle` component in `App.tsx`.
- **Token usage bug fix**: Task 20's own reference code had a bug (OpenAI's `total_tokens` JSON field is snake_case, but the C# DTO's case-insensitive-only deserialization didn't bridge the underscore) that would have silently recorded 0 tokens for every request in production; fixed with an explicit `[JsonPropertyName("total_tokens")]` attribute, caught by task review before merge.

## Demo script for handover
1. Create a project via `/admin` pointing at a small real KB folder + one small GitHub repo.
2. Trigger ingestion, wait ~30s–2min depending on repo size.
3. From `/query`, ask a KB-answerable question — confirm citations point at the right file.
4. Ask a code-related question — confirm the Code Analyzer agent fires (check `intent` was `code_issue`) and code file citations appear.
5. Upload a screenshot with an error dialog and ask "why am I seeing this?" — confirm Vision findings feed into the draft.
6. Click Mark Useful / Escalate — confirm `App_Data/feedback.json` records the entry.
