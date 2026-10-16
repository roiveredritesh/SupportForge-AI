# SupportForge AI

**A plug-and-play, multi-project AI support agent pipeline.**

SupportForge AI helps support teams answer customer questions about product features, code behaviour, and error screenshots. It retrieves relevant knowledge-base (KB) content and source-code context, optionally reads an attached screenshot, and drafts a grounded answer with cited sources. It never invents facts, and it never leaks raw code or document text into the customer-facing draft.

[![CI](https://github.com/roiveredritesh/SupportForge-AI/actions/workflows/ci.yml/badge.svg)](https://github.com/roiveredritesh/SupportForge-AI/actions/workflows/ci.yml)

---

## Table of contents

- [Features](#features)
- [How it works](#how-it-works)
- [Tech stack](#tech-stack)
- [Repository layout](#repository-layout)
- [Getting started](#getting-started)
- [Configuration](#configuration)
- [Using SupportForge](#using-supportforge)
- [Testing](#testing)
- [Deployment](#deployment)
- [Documentation](#documentation)
- [Known limitations](#known-limitations)
- [Contributing](#contributing)
- [License](#license)

---

## Features

- **Multi-agent pipeline** built on the [Microsoft Agent Framework](https://github.com/microsoft/agent-framework) `Workflow` API: Triage, Freshness Gate, KB Researcher, Cross-Reference, Code Analyzer, Vision Analyzer and Drafter.
- **LLM-judge verifiers** check each specialist's output and can send it back for one retry before it reaches the drafter.
- **Multi-project by design.** Each project gets its own KB collection and code graph, so you can onboard new projects from the UI without code changes.
- **Pluggable LLM providers**: OpenAI, NVIDIA NIM, Azure OpenAI, Anthropic, AWS Bedrock and Ollama (self-hosted). Embeddings can use a different provider from chat, and an optional fallback provider covers outages.
- **Multiple KB sources**: local documents, Confluence, websites and GitHub folders, with incremental re-sync via webhooks and a scheduled job.
- **Code understanding**: repos are cloned and parsed into a Neo4j code knowledge graph, which agents query for code-related questions.
- **Screenshot analysis**: attach an error screenshot and its findings feed into the answer.
- **Freshness management**: staleness signals from GitHub and Confluence webhooks and from scheduled syncs.
- **Support workflow**: conversations, feedback (useful / not useful), escalation queue, token-usage tracking and org/employee management.
- **Vector store abstraction** (`IVectorStoreService`): Chroma locally, Pinecone for production.
- **React web app**: real-time streaming answers over SignalR, dark mode, and a settings area for projects, connected apps, employees, feedback and token usage.
- **Observability**: OpenTelemetry tracing, optional Azure Application Insights export, health checks, and API rate limiting.
- **Input guard** for prompt injection and jailbreak attempts, screened before intent classification. Off by default. It screens with a heuristic pass, and with Jev when `Jev:ApiKey` is set. Without a key it can flag turns but not block them. See the [configuration guide](docs/deployment/configuration-guide.md#input-guard-prompt-injection-and-jailbreak).
- **Jev intent classification** (optional): `Triage:Classifier` selects `Llm` (the default), `Jev`, or `Shadow`, which runs Jev alongside the LLM so the two can be compared. Off by default, since it needs a TypeSafe key. See the [configuration guide](docs/deployment/configuration-guide.md#triage-classifier).
- **Jev findings validation** (optional): `Verification:Judge` selects `Llm` (the default), `Jev`, or `Shadow` for the judge that checks whether retrieved KB snippets and vision findings answer the question. Jev scores each snippet, so weak matches are dropped. Off by default, since it needs a TypeSafe key. See the [configuration guide](docs/deployment/configuration-guide.md#findings-verification-verification).
- **Eval harness** (`SupportForge.Evals`) for validating judge and classifier quality before changing models.

## How it works

```
                      +--> KbResearcher --> KbResearcherVerifier --> CrossReference --> CodeAnalyzer --> CodeAnalyzerVerifier --+
Query --> Triage --> FreshnessGate                                                                                            +--> Merge --> Drafter --> Answer + citations
                      +--> VisionAnalyzer (only if a screenshot is attached) --> VisionAnalyzerVerifier -------------------------+
```

1. **Triage** classifies the intent of the query.
2. **FreshnessGate** flags stale knowledge sources (no LLM call).
3. **KbResearcher** retrieves KB snippets from the vector store. Its verifier judges them.
4. **CrossReference** extracts code terms from the KB hits. **CodeAnalyzer** then queries the Neo4j code graph, and its verifier judges the result.
5. **VisionAnalyzer** reads the screenshot, if one was attached.
6. **Drafter** merges the verified results into a grounded answer, with a guard against leaking raw code or docs.

See [`docs/architecture/2026-08-06-001-hld-system-design.md`](docs/architecture/2026-08-06-001-hld-system-design.md) for the full diagram and design discussion.

## Tech stack

| Layer | Technology |
|---|---|
| Backend | .NET 9, ASP.NET Core, SignalR, JWT auth |
| Agent orchestration | Microsoft Agent Framework |
| Vector store | Chroma (local) / Pinecone (production) |
| Code graph | Neo4j 5 |
| Frontend | React 19, TypeScript, Vite, Tailwind CSS, TanStack Query, Zustand |
| Tests | xUnit (backend), Vitest + Testing Library (frontend) |
| CI / packaging | GitHub Actions, Docker |

## Repository layout

```
backend/
  SupportForge.Api/           ASP.NET Core host: controllers, SignalR hub, auth, health checks
  SupportForge.Agents/        Agents, verifiers, CoordinatorPipeline, LLM clients
  SupportForge.Ingestion/     KB + code ingestion jobs, code graph extraction, schedulers
  SupportForge.VectorStore/   Vector store abstraction (Chroma, Pinecone)
  SupportForge.Core/          Domain models and interfaces
  SupportForge.Common/        Shared utilities
  SupportForge.Evals/         Eval harness for judges and classifiers
  SupportForge.Api.Tests/     Backend test suite
frontend/                     React + Vite single-page app
Scripts/                      Local DB helpers and deploy script (PowerShell)
docs/                         Architecture, deployment runbooks, plans
doc/                          Original PRD and TSD
docker-compose.yml            Local Chroma + Neo4j
Dockerfile                    Backend API image
```

## Getting started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Node.js 20+](https://nodejs.org/) and npm
- [Docker](https://www.docker.com/) (for Chroma and Neo4j), or a local Chroma and Neo4j install
- An API key for at least one [supported LLM provider](#llm-providers), or a local [Ollama](https://ollama.com/) install

### 1. Clone the repository

```bash
git clone https://github.com/roiveredritesh/SupportForge-AI.git
cd SupportForge-AI
```

### 2. Start Chroma and Neo4j

```bash
docker compose up -d
```

This starts:

- **Chroma** at `http://localhost:8000`
- **Neo4j** at `bolt://localhost:7687` (browser UI at `http://localhost:7474`), with auth disabled for local development

Without Docker, `Scripts/Start-LocalDbs.ps1` starts local installs of both on Windows. `Scripts/Stop-LocalDbs.ps1` stops them.

### 3. Configure the backend

Secrets are never committed. Supply them with [user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets) or environment variables.

```bash
cd backend/SupportForge.Api

# Pick a provider and set its key (example: OpenAI)
dotnet user-secrets set "Llm:Provider" "OpenAI"
dotnet user-secrets set "Llm:OpenAI:ApiKey" "sk-..."

# A stable JWT signing key (use a long random string)
dotnet user-secrets set "Jwt:Key" "<at-least-32-random-characters>"
```

As environment variables, replace `:` with `__`, for example `Llm__OpenAI__ApiKey`.

> If `Jwt:Key` is unset the API generates a random key per process. That works for a quick try-out, but tokens won't survive a restart.

### 4. Run the backend

```bash
cd backend
dotnet run --project SupportForge.Api
```

The API listens on `http://localhost:5080` and `https://localhost:5001` (see `Properties/launchSettings.json`).

### 5. Run the frontend

```bash
cd frontend
npm ci
npm run dev
```

The frontend calls `https://localhost:5001/api` by default. To point it elsewhere, create `frontend/.env`:

```
VITE_API_BASE_URL=http://localhost:5080/api
```

Open the URL Vite prints (usually `http://localhost:5173`), register an account, and continue to [Using SupportForge](#using-supportforge).

### Run the backend with Docker

```bash
docker build -t supportforge-api .
docker run -p 8080:8080 \
  -e Llm__Provider=OpenAI \
  -e Llm__OpenAI__ApiKey=sk-... \
  -e Jwt__Key=<long-random-string> \
  -e VectorStore__Chroma__BaseUrl=http://host.docker.internal:8000 \
  -e Neo4j__Uri=bolt://host.docker.internal:7687 \
  supportforge-api
```

## Configuration

Configuration lives in [`backend/SupportForge.Api/appsettings.json`](backend/SupportForge.Api/appsettings.json). Every setting can be overridden with user-secrets or environment variables. Full details are in the [configuration guide](docs/deployment/configuration-guide.md).

### LLM providers

`Llm:Provider` selects the chat and vision provider.

| Provider | Chat | Vision | Embeddings | Notes |
|---|---|---|---|---|
| `OpenAI` | yes | yes | yes | |
| `NvidiaNim` | yes | yes | yes | Current default in `appsettings.json` |
| `Azure` | yes | yes | yes | Model settings are deployment names |
| `Anthropic` | yes | yes | no | Set `Embeddings:Provider` to a different provider |
| `Bedrock` | yes | yes | yes | Uses the standard AWS credential chain, no API key |
| `Ollama` | yes | yes | yes | Self-hosted, no API key |

Example: Anthropic for chat and vision, with NVIDIA NIM for embeddings.

```json
"Llm": { "Provider": "Anthropic", "Anthropic": { "ApiKey": "sk-ant-..." } },
"Embeddings": { "Provider": "NvidiaNim" }
```

Optional settings:

- `Llm:FallbackProvider`: fail over to a second provider when the primary's circuit breaker is open.
- `Llm:<Provider>:CheapChatModel`: a cheaper model for Triage and the verifiers. Validate it with the eval harness first.

### Other key settings

| Key | Purpose |
|---|---|
| `VectorStore:Provider` | `Chroma` (default) or `Pinecone` |
| `VectorStore:Chroma:BaseUrl` | Chroma server URL |
| `VectorStore:Pinecone:Host` / `ApiKey` | Pinecone index URL and key |
| `Neo4j:Uri` / `User` / `Password` / `Database` | Code graph connection |
| `Jwt:Key` / `Issuer` / `Audience` | JWT signing |
| `GitHubWebhook:Secret` | Verifies GitHub webhook signatures. Until set, the webhook endpoint returns 503. |
| `ConfluenceWebhook:Secret` | Shared secret for the Confluence webhook. Until set, the endpoint returns 503. |
| `Freshness:ScheduledSyncIntervalHours` | Fallback re-sync interval for non-webhook KB sources |
| `Drafter:GroundednessCheckEnabled` | Extra LLM-judge check on draft claims (off by default) |
| `Guard:Mode` (and the rest of `Guard`) | Input guard for prompt injection and jailbreaks: `Off` (default), `Shadow` or `Enforce`. Uses Jev when `Jev:ApiKey` is set. Without a key it is heuristic-only, so it can flag but not block. See the configuration guide. |
| `Triage:Classifier` | Intent classifier: `Llm` (default), `Jev`, or `Shadow` (Jev runs alongside the LLM and is logged, but the LLM answer is used). `Jev` and `Shadow` need `Jev:ApiKey`. See the configuration guide. |
| `Verification:Judge` | Findings judge for the KB and vision verifiers (and the Drafter's groundedness check): `Llm` (default), `Jev`, or `Shadow`. `Jev` and `Shadow` need `Jev:ApiKey`, and send the question, recent history and KB snippet text to TypeSafe. See the configuration guide. |
| `Verification:Jev:IncludeCode` | Also sends code-graph snippets (customer identifiers and file paths) to Jev. Off by default. Matters only when `Verification:Judge` is `Jev` or `Shadow`. |
| `Jev:ApiKey` | TypeSafe Jev key, which turns on Jev-backed triage and guard screening. Set it through `Jev__ApiKey` or a secret store, never in a config file. Blank by default. |
| `RateLimiting:PermitLimit` / `WindowSeconds` | API rate limiting |
| `ApplicationInsights:ConnectionString` | Optional Azure Monitor export |

## Using SupportForge

1. **Create a project.** Go to **Settings → Projects**, choose a unique lowercase Project ID (it prefixes the vector collection) and a display name. Add KB sources and, optionally, a GitHub repo.
2. **Ingest.** Trigger a re-index and wait for ingestion to finish. Check the project's freshness status to confirm.
3. **Ask a question.** Open the query page, select the project, type a question, and optionally attach a screenshot.
4. **Review the answer.** You get a grounded draft with cited sources, a confidence score and a freshness indicator. Mark it useful or not useful, or escalate it to an engineer.
5. **Monitor.** Use the settings pages for feedback trends and token usage per project.

### Connecting webhooks (optional)

Point a GitHub webhook at `POST /api/webhooks/github` (set `GitHubWebhook:Secret` to match), and a Confluence webhook at `POST /api/webhooks/confluence?secret=...` (set `ConfluenceWebhook:Secret`). Changes then trigger incremental re-ingestion.

## Testing

```bash
# Backend
dotnet test backend/SupportForge.Backend.sln

# Frontend
cd frontend
npm run lint
npx vitest run
npm run build   # typecheck + production build
```

One end-to-end backend test needs a live Chroma instance and an `OpenAI__ApiKey`. It skips automatically when they aren't available.

The eval harness in `backend/SupportForge.Evals` measures LLM-judge and classifier quality. Run it before switching models or enabling cheaper model tiers.

## Deployment

CI builds, tests and packages the backend and frontend on every push. See:

- [`docs/deployment/backend-runbook.md`](docs/deployment/backend-runbook.md)
- [`docs/deployment/frontend-runbook.md`](docs/deployment/frontend-runbook.md)
- [`Scripts/Deploy.ps1`](Scripts/Deploy.ps1) (Windows/IIS target)

The `Dockerfile` builds the API as a container image. The frontend is a static bundle (`npm run build`) that can be served from any static host, as long as unknown routes are rewritten to `index.html`.

## Documentation

| Doc | Description |
|---|---|
| [PRD](doc/PRD.md) / [TSD](doc/TSD.md) | Original product and technical specifications |
| [System design (HLD)](docs/architecture/2026-08-06-001-hld-system-design.md) | Current architecture |
| [Agentic pipeline](docs/agentic-pipeline.md) | How the Agent Framework workflow is built (the graph shape is partly outdated, so use the HLD for topology) |
| [Configuration guide](docs/deployment/configuration-guide.md) | All configuration options |
| [Gap analysis](docs/architecture/2026-08-06-002-gap-analysis.md) | Known gaps and roadmap |

## Known limitations

SupportForge is under active development. Before you deploy it, be aware that:

- Authentication is local JWT only. Entra ID / SSO is not implemented.
- Private GitHub repo access is modelled but not fully wired to a secret store.
- The ingestion queue is in-process and in-memory, so it isn't persisted and doesn't scale across instances.
- Projects, feedback and token usage are stored in JSON files, not a database.
- Slack/Teams integration is on the roadmap but not available yet.
- Without `Jev:ApiKey`, the input guard is heuristic-only, so in every mode it can flag turns but never block them. With the key set, Jev-backed screening and triage send customer messages and recent history to TypeSafe. Setting `Verification:Judge` to `Jev` or `Shadow` also sends KB snippet text, and code-graph snippets when `Verification:Jev:IncludeCode` is true.

See the [gap analysis](docs/architecture/2026-08-06-002-gap-analysis.md) for more detail. Some of the older docs predate recent changes, so check the code if something seems out of sync.

## Contributing

Contributions are welcome.

1. Fork the repo and create a branch from `master`.
2. Make your change, with tests where it makes sense.
3. Make sure `dotnet test` and the frontend lint, tests and build pass. CI runs the same checks.
4. Open a pull request describing what you changed and why.

For bigger changes, please open an issue first to discuss the approach. Never commit API keys, signing keys or webhook secrets.

## License

No license file is included yet. Before open-sourcing, add a `LICENSE` file (for example [MIT](https://choosealicense.com/licenses/mit/) or [Apache-2.0](https://choosealicense.com/licenses/apache-2.0/)) and replace this section with the license name.
