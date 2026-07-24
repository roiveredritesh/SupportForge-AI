# SupportForge AI MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a working end-to-end SupportForge AI MVP in 7 days: support engineers pick a project, ask a question (optionally with a screenshot), and get a cited, drafted answer from a multi-agent pipeline backed by KB + code search, with a React UI and basic admin config.

**Architecture:** ASP.NET Core 8 Web API (`backend/SupportForge.Api`) hosting a multi-agent pipeline (`SupportForge.Agents`) that calls tool services (KB search, code search, vision) backed by a vector-store abstraction (`SupportForge.VectorStore`, Chroma locally / Pinecone in prod) and an ingestion background worker (`SupportForge.Ingestion`). A React 18 + TypeScript + Vite frontend (`frontend/`) talks to the API via TanStack Query + Axios, with Zustand for local UI state. Both are deployed to a single EC2 Windows instance under IIS.

**Tech Stack:** .NET 8, ASP.NET Core Web API, LibGit2Sharp, Chroma (HTTP API, local Docker) / Pinecone (.NET REST client), React 18 + TypeScript + Vite, Tailwind + shadcn/ui, TanStack Query, Zustand, Axios, xUnit, Vitest + React Testing Library, Microsoft Entra ID (JWT bearer).

## Global Constraints

- Target completion: 7 days (per TSD section 5) — one calendar week from repo start.
- Performance: end-to-end query response < 8–12 seconds for most queries (PRD §7).
- Security: Entra ID authentication, role-based access (Support vs Admin) (PRD §7, TSD §2).
- Hosting: EC2 Windows + IIS for backend; frontend served statically or via the same IIS site (PRD §7, TSD §1).
- Scalability target: 10+ projects initially (PRD §7) — every domain model is keyed by `projectId`, never assumes a single project.
- Vector DB must be abstracted behind `IVectorStoreService` so Chroma (local) and Pinecone (prod) are swappable via config, never referenced directly by callers (PRD §6, TSD §2).
- Frontend stack is fixed: React 18 + TypeScript + Vite + Tailwind/shadcn/ui + TanStack Query + Zustand + Axios (PRD §5, TSD §4) — do not substitute other state/data libraries.
- UI must support dark/light mode, loading skeletons, and error boundaries with retry (TSD §4).
- API surface for MVP is exactly: `POST /api/chat/query`, `GET /api/projects`, `POST /api/projects`, `POST /api/ingestion/trigger` (TSD §6) — extensions only where a task below explicitly needs a sub-route (e.g. feedback, admin KB sources) for screens the PRD explicitly requires.
- **Assumption flag:** "Microsoft Agent Framework" is referenced by the PRD/TSD as the agent engine, but no concrete 2026 SDK surface is available to code against safely. This plan implements a small first-party orchestration abstraction (`IAgent`, `AgentContext`, `AgentPipeline`) that mirrors the PRD's Triage → KB Researcher/Code Analyzer/Vision Analyzer → Drafter → Coordinator shape. Swapping the internals for the real Microsoft Agent Framework later only touches `SupportForge.Agents` — no other project depends on its internal types. Call this out to the team as a follow-up integration point, not a gap in this plan.

---

## Day 1 — Foundation

### Task 1: Solution & repo scaffolding

**Files:**
- Create: `SupportForge.AI.sln`
- Create: `backend/SupportForge.Api/SupportForge.Api.csproj`
- Create: `backend/SupportForge.Api/Program.cs`
- Create: `backend/SupportForge.Api/appsettings.json`
- Create: `backend/SupportForge.Api/appsettings.Development.json`
- Create: `backend/SupportForge.Core/SupportForge.Core.csproj`
- Create: `backend/SupportForge.Agents/SupportForge.Agents.csproj`
- Create: `backend/SupportForge.Ingestion/SupportForge.Ingestion.csproj`
- Create: `backend/SupportForge.VectorStore/SupportForge.VectorStore.csproj`
- Create: `backend/SupportForge.Common/SupportForge.Common.csproj`
- Create: `backend/SupportForge.Api.Tests/SupportForge.Api.Tests.csproj`
- Create: `frontend/` (Vite React-TS scaffold)
- Create: `.gitignore`

**Interfaces:**
- Produces: solution builds (`dotnet build`), API runs on `https://localhost:5001`, health check at `GET /health` returns `200 OK`.
- Produces: `frontend` dev server runs on `http://localhost:5173` and shows the default Vite+React page (to be replaced Day 5).

- [ ] **Step 1: Create the .NET solution and projects**

```bash
cd "D:/New folder/agentic-framework/Support-pipeline"
dotnet new sln -n SupportForge.AI
dotnet new webapi -n SupportForge.Api -o backend/SupportForge.Api --use-controllers
dotnet new classlib -n SupportForge.Core -o backend/SupportForge.Core
dotnet new classlib -n SupportForge.Agents -o backend/SupportForge.Agents
dotnet new classlib -n SupportForge.Ingestion -o backend/SupportForge.Ingestion
dotnet new classlib -n SupportForge.VectorStore -o backend/SupportForge.VectorStore
dotnet new classlib -n SupportForge.Common -o backend/SupportForge.Common
dotnet new xunit -n SupportForge.Api.Tests -o backend/SupportForge.Api.Tests
dotnet sln add backend/SupportForge.Api backend/SupportForge.Core backend/SupportForge.Agents backend/SupportForge.Ingestion backend/SupportForge.VectorStore backend/SupportForge.Common backend/SupportForge.Api.Tests
dotnet add backend/SupportForge.Api reference backend/SupportForge.Core backend/SupportForge.Agents backend/SupportForge.VectorStore backend/SupportForge.Common
dotnet add backend/SupportForge.Agents reference backend/SupportForge.Core backend/SupportForge.VectorStore backend/SupportForge.Common
dotnet add backend/SupportForge.Ingestion reference backend/SupportForge.Core backend/SupportForge.VectorStore backend/SupportForge.Common
dotnet add backend/SupportForge.VectorStore reference backend/SupportForge.Common
dotnet add backend/SupportForge.Api.Tests reference backend/SupportForge.Api
```

- [ ] **Step 2: Wire minimal `Program.cs` with a health endpoint**

`backend/SupportForge.Api/Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Frontend");
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
```

- [ ] **Step 3: Verify the API builds and runs**

Run: `dotnet build SupportForge.AI.sln`
Expected: `Build succeeded. 0 Error(s)`

Run: `dotnet run --project backend/SupportForge.Api`
Expected: console shows `Now listening on: https://localhost:5001`; `curl -k https://localhost:5001/health` returns `{"status":"ok"}`.

- [ ] **Step 4: Scaffold the React frontend**

```bash
cd "D:/New folder/agentic-framework/Support-pipeline"
npm create vite@latest frontend -- --template react-ts
cd frontend
npm install
npm install -D tailwindcss postcss autoprefixer
npx tailwindcss init -p
npm install @tanstack/react-query axios zustand react-router-dom
```

Update `frontend/tailwind.config.js` `content` array to `["./index.html", "./src/**/*.{ts,tsx}"]`, and add the standard `@tailwind base; @tailwind components; @tailwind utilities;` directives to `frontend/src/index.css`.

- [ ] **Step 5: Verify the frontend dev server runs**

Run: `npm run dev` (from `frontend/`)
Expected: server starts on `http://localhost:5173`, browser shows the Vite+React starter page with Tailwind loaded (no console errors).

- [ ] **Step 6: Add `.gitignore` and initialize git**

`.gitignore`:

```
bin/
obj/
node_modules/
dist/
.vs/
*.user
appsettings.Development.json
frontend/.env
```

```bash
git init
git add SupportForge.AI.sln backend frontend .gitignore
git commit -m "chore: scaffold .NET solution and React frontend"
```

---

### Task 2: `IVectorStoreService` abstraction + Chroma implementation

**Files:**
- Create: `backend/SupportForge.VectorStore/IVectorStoreService.cs`
- Create: `backend/SupportForge.VectorStore/Models/VectorDocument.cs`
- Create: `backend/SupportForge.VectorStore/Models/VectorQueryResult.cs`
- Create: `backend/SupportForge.VectorStore/Chroma/ChromaVectorStoreService.cs`
- Create: `backend/SupportForge.VectorStore/Chroma/ChromaOptions.cs`
- Create: `backend/SupportForge.VectorStore/VectorStoreServiceCollectionExtensions.cs`
- Test: `backend/SupportForge.Api.Tests/VectorStore/ChromaVectorStoreServiceTests.cs`

**Interfaces:**
- Produces: `IVectorStoreService` with `UpsertAsync`, `QueryAsync`, `DeleteAsync`, `DeleteCollectionAsync` — this is what `SupportForge.Agents` tools and `SupportForge.Ingestion` consume for the rest of the plan.
- Consumes: nothing (first vertical slice).

- [ ] **Step 1: Define the shared models**

`backend/SupportForge.VectorStore/Models/VectorDocument.cs`:

```csharp
namespace SupportForge.VectorStore.Models;

public sealed record VectorDocument(
    string Id,
    string Text,
    float[] Embedding,
    IReadOnlyDictionary<string, string> Metadata);
```

`backend/SupportForge.VectorStore/Models/VectorQueryResult.cs`:

```csharp
namespace SupportForge.VectorStore.Models;

public sealed record VectorQueryResult(
    string Id,
    string Text,
    float Score,
    IReadOnlyDictionary<string, string> Metadata);
```

- [ ] **Step 2: Define `IVectorStoreService`**

`backend/SupportForge.VectorStore/IVectorStoreService.cs`:

```csharp
using SupportForge.VectorStore.Models;

namespace SupportForge.VectorStore;

public interface IVectorStoreService
{
    Task UpsertAsync(string collection, IReadOnlyList<VectorDocument> documents, CancellationToken ct = default);

    Task<IReadOnlyList<VectorQueryResult>> QueryAsync(
        string collection,
        float[] queryEmbedding,
        int topK,
        IReadOnlyDictionary<string, string>? metadataFilter = null,
        CancellationToken ct = default);

    Task DeleteAsync(string collection, IReadOnlyList<string> ids, CancellationToken ct = default);

    Task DeleteCollectionAsync(string collection, CancellationToken ct = default);
}
```

`collection` is always `{projectId}-kb` or `{projectId}-code`, keeping every project's vectors isolated — this satisfies the multi-project scalability constraint without per-project infra.

- [ ] **Step 3: Write the failing test for the Chroma implementation**

`backend/SupportForge.Api.Tests/VectorStore/ChromaVectorStoreServiceTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using SupportForge.VectorStore.Chroma;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.VectorStore;

public class ChromaVectorStoreServiceTests
{
    [Fact]
    public async Task QueryAsync_ParsesChromaResponse_IntoVectorQueryResults()
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "ids": [["doc-1"]],
                  "documents": [["hello world"]],
                  "distances": [[0.12]],
                  "metadatas": [[{"source": "kb"}]]
                }
                """)
            });

        var client = new HttpClient(handler.Object) { BaseAddress = new Uri("http://localhost:8000") };
        var options = Options.Create(new ChromaOptions { BaseUrl = "http://localhost:8000" });
        var sut = new ChromaVectorStoreService(client, options);

        var results = await sut.QueryAsync("proj1-kb", new float[] { 0.1f, 0.2f }, topK: 1);

        Assert.Single(results);
        Assert.Equal("doc-1", results[0].Id);
        Assert.Equal("hello world", results[0].Text);
        Assert.Equal("kb", results[0].Metadata["source"]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend/SupportForge.Api.Tests --filter ChromaVectorStoreServiceTests`
Expected: FAIL — `ChromaVectorStoreService` and `ChromaOptions` do not exist yet.

Add `Moq` NuGet package to the test project first: `dotnet add backend/SupportForge.Api.Tests package Moq`.

- [ ] **Step 3: Implement `ChromaOptions` and `ChromaVectorStoreService`**

`backend/SupportForge.VectorStore/Chroma/ChromaOptions.cs`:

```csharp
namespace SupportForge.VectorStore.Chroma;

public sealed class ChromaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8000";
}
```

`backend/SupportForge.VectorStore/Chroma/ChromaVectorStoreService.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SupportForge.VectorStore.Models;

namespace SupportForge.VectorStore.Chroma;

public sealed class ChromaVectorStoreService : IVectorStoreService
{
    private readonly HttpClient _http;

    public ChromaVectorStoreService(HttpClient http, IOptions<ChromaOptions> options)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(options.Value.BaseUrl);
    }

    public async Task UpsertAsync(string collection, IReadOnlyList<VectorDocument> documents, CancellationToken ct = default)
    {
        await EnsureCollectionAsync(collection, ct);

        var payload = new
        {
            ids = documents.Select(d => d.Id).ToArray(),
            documents = documents.Select(d => d.Text).ToArray(),
            embeddings = documents.Select(d => d.Embedding).ToArray(),
            metadatas = documents.Select(d => d.Metadata).ToArray(),
        };

        var response = await _http.PostAsJsonAsync($"/api/v1/collections/{collection}/upsert", payload, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<VectorQueryResult>> QueryAsync(
        string collection,
        float[] queryEmbedding,
        int topK,
        IReadOnlyDictionary<string, string>? metadataFilter = null,
        CancellationToken ct = default)
    {
        var payload = new
        {
            query_embeddings = new[] { queryEmbedding },
            n_results = topK,
            where = metadataFilter,
        };

        var response = await _http.PostAsJsonAsync($"/api/v1/collections/{collection}/query", payload, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChromaQueryResponse>(cancellationToken: ct)
                   ?? throw new InvalidOperationException("Empty Chroma response");

        var results = new List<VectorQueryResult>();
        for (var i = 0; i < body.Ids[0].Count; i++)
        {
            results.Add(new VectorQueryResult(
                body.Ids[0][i],
                body.Documents[0][i],
                body.Distances[0][i],
                body.Metadatas[0][i]));
        }

        return results;
    }

    public async Task DeleteAsync(string collection, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/v1/collections/{collection}/delete", new { ids }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteCollectionAsync(string collection, CancellationToken ct = default)
    {
        var response = await _http.DeleteAsync($"/api/v1/collections/{collection}", ct);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    private async Task EnsureCollectionAsync(string collection, CancellationToken ct)
    {
        var response = await _http.PostAsJsonAsync("/api/v1/collections", new { name = collection, get_or_create = true }, ct);
        response.EnsureSuccessStatusCode();
    }

    private sealed class ChromaQueryResponse
    {
        public List<List<string>> Ids { get; set; } = new();
        public List<List<string>> Documents { get; set; } = new();
        public List<List<float>> Distances { get; set; } = new();
        public List<List<Dictionary<string, string>>> Metadatas { get; set; } = new();
    }
}
```

- [ ] **Step 4: Register DI extension**

`backend/SupportForge.VectorStore/VectorStoreServiceCollectionExtensions.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SupportForge.VectorStore.Chroma;

namespace SupportForge.VectorStore;

public static class VectorStoreServiceCollectionExtensions
{
    public static IServiceCollection AddVectorStore(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["VectorStore:Provider"] ?? "Chroma";

        if (provider == "Chroma")
        {
            services.Configure<ChromaOptions>(config.GetSection("VectorStore:Chroma"));
            services.AddHttpClient<IVectorStoreService, ChromaVectorStoreService>();
        }
        else
        {
            throw new NotSupportedException($"Vector store provider '{provider}' is not registered yet (Pinecone lands Day 7).");
        }

        return services;
    }
}
```

Add to `backend/SupportForge.Api/Program.cs` before `builder.Build()`: `builder.Services.AddVectorStore(builder.Configuration);`

Add to `appsettings.json`:

```json
{
  "VectorStore": {
    "Provider": "Chroma",
    "Chroma": { "BaseUrl": "http://localhost:8000" }
  }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test backend/SupportForge.Api.Tests --filter ChromaVectorStoreServiceTests -v normal`
Expected: PASS (1 test passed).

- [ ] **Step 6: Commit**

```bash
git add backend/SupportForge.VectorStore backend/SupportForge.Api backend/SupportForge.Api.Tests
git commit -m "feat: add IVectorStoreService abstraction with Chroma implementation"
```

---

### Task 3: Project config system (entity + CRUD API)

**Files:**
- Create: `backend/SupportForge.Core/Entities/Project.cs`
- Create: `backend/SupportForge.Core/Entities/GitHubRepoConfig.cs`
- Create: `backend/SupportForge.Core/Entities/KbSourceConfig.cs`
- Create: `backend/SupportForge.Core/IProjectRepository.cs`
- Create: `backend/SupportForge.Core/JsonFileProjectRepository.cs`
- Create: `backend/SupportForge.Api/Controllers/ProjectsController.cs`
- Test: `backend/SupportForge.Api.Tests/Controllers/ProjectsControllerTests.cs`

**Interfaces:**
- Produces: `IProjectRepository` (`GetAllAsync`, `GetByIdAsync`, `UpsertAsync`) — consumed by `ProjectsController` now, and by `SupportForge.Ingestion` (Task 6+) and `SupportForge.Agents` (Task 4+) to look up per-project GitHub/KB config.
- Produces: `GET /api/projects`, `POST /api/projects` (TSD §6).

- [ ] **Step 1: Define the `Project` entity and sub-configs**

`backend/SupportForge.Core/Entities/GitHubRepoConfig.cs`:

```csharp
namespace SupportForge.Core.Entities;

public sealed record GitHubRepoConfig(string Owner, string Repo, string DefaultBranch, string? AccessTokenSecretName);
```

`backend/SupportForge.Core/Entities/KbSourceConfig.cs`:

```csharp
namespace SupportForge.Core.Entities;

public enum KbSourceType { Documents, Confluence }

public sealed record KbSourceConfig(KbSourceType Type, string Location, DateTimeOffset? LastSyncedAt);
```

`backend/SupportForge.Core/Entities/Project.cs`:

```csharp
namespace SupportForge.Core.Entities;

public sealed class Project
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public List<GitHubRepoConfig> Repos { get; init; } = new();
    public List<KbSourceConfig> KbSources { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 2: Define `IProjectRepository` and a JSON-file-backed implementation**

MVP has no DB provisioned yet (not in the 7-day critical path per TSD §7 risk mitigation: "prioritize working UI over perfect admin"), so persistence is a single JSON file under a data directory — swappable later behind the interface without touching callers.

`backend/SupportForge.Core/IProjectRepository.cs`:

```csharp
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface IProjectRepository
{
    Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken ct = default);
    Task<Project?> GetByIdAsync(string id, CancellationToken ct = default);
    Task UpsertAsync(Project project, CancellationToken ct = default);
}
```

`backend/SupportForge.Core/JsonFileProjectRepository.cs`:

```csharp
using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed class JsonFileProjectRepository : IProjectRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileProjectRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "projects.json");
    }

    public async Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return Array.Empty<Project>();

        await _lock.WaitAsync(ct);
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<Project>>(stream, cancellationToken: ct) ?? new();
        }
        finally { _lock.Release(); }
    }

    public async Task<Project?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct);
        return all.FirstOrDefault(p => p.Id == id);
    }

    public async Task UpsertAsync(Project project, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<List<Project>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
                : new List<Project>();

            all.RemoveAll(p => p.Id == project.Id);
            all.Add(project);

            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);
        }
        finally { _lock.Release(); }
    }
}
```

- [ ] **Step 3: Write the failing controller test**

`backend/SupportForge.Api.Tests/Controllers/ProjectsControllerTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class ProjectsControllerTests
{
    [Fact]
    public async Task CreateProject_Then_ListProjects_ReturnsCreatedProject()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileProjectRepository(tempDir);
        var controller = new ProjectsController(repo);

        var project = new Project { Id = "proj1", Name = "Test Project" };
        await controller.CreateOrUpdate(project);

        var result = await controller.GetAll();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var projects = Assert.IsAssignableFrom<IReadOnlyList<Project>>(ok.Value);

        Assert.Single(projects);
        Assert.Equal("Test Project", projects[0].Name);

        Directory.Delete(tempDir, recursive: true);
    }
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test backend/SupportForge.Api.Tests --filter ProjectsControllerTests`
Expected: FAIL — `ProjectsController` does not exist.

- [ ] **Step 5: Implement `ProjectsController`**

`backend/SupportForge.Api/Controllers/ProjectsController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/projects")]
public class ProjectsController : ControllerBase
{
    private readonly IProjectRepository _repo;

    public ProjectsController(IProjectRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<Project>>> GetAll(CancellationToken ct)
        => Ok(await _repo.GetAllAsync(ct));

    [HttpPost]
    public async Task<ActionResult<Project>> CreateOrUpdate(Project project, CancellationToken ct)
    {
        await _repo.UpsertAsync(project, ct);
        return Ok(project);
    }
}
```

Register in `Program.cs`:

```csharp
builder.Services.AddSingleton<IProjectRepository>(
    new JsonFileProjectRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test backend/SupportForge.Api.Tests --filter ProjectsControllerTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add backend/SupportForge.Core backend/SupportForge.Api
git commit -m "feat: add project config entity, repository, and CRUD API"
```

---

## Day 2 — Agent Core + Simple Workflow

### Task 4: Agent orchestration abstraction + Triage agent

**Files:**
- Create: `backend/SupportForge.Agents/IAgent.cs`
- Create: `backend/SupportForge.Agents/AgentContext.cs`
- Create: `backend/SupportForge.Agents/ILlmClient.cs`
- Create: `backend/SupportForge.Agents/OpenAiLlmClient.cs`
- Create: `backend/SupportForge.Agents/TriageAgent.cs`
- Test: `backend/SupportForge.Api.Tests/Agents/TriageAgentTests.cs`

**Interfaces:**
- Consumes: nothing new (first agent).
- Produces: `IAgent` (`Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct)`), `AgentContext` (mutable bag carrying query, project, intermediate results), `ILlmClient` (`Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)`) — every later agent (Drafter, KB Researcher, Code Analyzer, Vision Analyzer, Coordinator) implements `IAgent` and calls `ILlmClient`.

- [ ] **Step 1: Define `AgentContext`, `IAgent`, `ILlmClient`**

`backend/SupportForge.Agents/AgentContext.cs`:

```csharp
namespace SupportForge.Agents;

public sealed class AgentContext
{
    public required string ProjectId { get; init; }
    public required string Query { get; init; }
    public string? ScreenshotBase64 { get; init; }

    public string Intent { get; set; } = string.Empty;
    public List<string> KbSnippets { get; } = new();
    public List<string> CodeSnippets { get; } = new();
    public string? VisionFindings { get; set; }
    public string Draft { get; set; } = string.Empty;
    public List<(string Label, string Url)> Sources { get; } = new();
    public double Confidence { get; set; }
}
```

`backend/SupportForge.Agents/IAgent.cs`:

```csharp
namespace SupportForge.Agents;

public interface IAgent
{
    string Name { get; }
    Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default);
}
```

`backend/SupportForge.Agents/ILlmClient.cs`:

```csharp
namespace SupportForge.Agents;

public interface ILlmClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing test for `TriageAgent`**

`backend/SupportForge.Api.Tests/Agents/TriageAgentTests.cs`:

```csharp
using Moq;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class TriageAgentTests
{
    [Fact]
    public async Task RunAsync_SetsIntent_FromLlmResponse()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .ReturnsAsync("code_issue");

        var agent = new TriageAgent(llm.Object);
        var context = new AgentContext { ProjectId = "proj1", Query = "Getting a 500 error on checkout" };

        var result = await agent.RunAsync(context);

        Assert.Equal("code_issue", result.Intent);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test backend/SupportForge.Api.Tests --filter TriageAgentTests`
Expected: FAIL — `TriageAgent` does not exist.

- [ ] **Step 4: Implement `TriageAgent`**

`backend/SupportForge.Agents/TriageAgent.cs`:

```csharp
namespace SupportForge.Agents;

public sealed class TriageAgent : IAgent
{
    private readonly ILlmClient _llm;
    public string Name => "Triage";

    public TriageAgent(ILlmClient llm) => _llm = llm;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        const string systemPrompt = """
            You classify support queries into exactly one label: "kb_question", "code_issue", or "screenshot_error".
            Respond with only the label, nothing else.
            """;

        var intent = await _llm.CompleteAsync(systemPrompt, context.Query, ct);
        context.Intent = intent.Trim();
        return context;
    }
}
```

- [ ] **Step 5: Implement `OpenAiLlmClient`** (real network calls, used outside unit tests via DI)

`backend/SupportForge.Agents/OpenAiLlmClient.cs`:

```csharp
using System.Net.Http.Json;

namespace SupportForge.Agents;

public sealed class OpenAiLlmClient : ILlmClient
{
    private readonly HttpClient _http;

    public OpenAiLlmClient(HttpClient http) => _http = http;

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("chat/completions", new
        {
            model = "gpt-4o-mini",
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        }, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
        return body?.Choices.FirstOrDefault()?.Message.Content?.Trim() ?? string.Empty;
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("embeddings", new { model = "text-embedding-3-small", input = text }, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct);
        return body?.Data.FirstOrDefault()?.Embedding ?? Array.Empty<float>();
    }

    private sealed class ChatResponse { public List<Choice> Choices { get; set; } = new(); }
    private sealed class Choice { public Message Message { get; set; } = new(); }
    private sealed class Message { public string? Content { get; set; } }
    private sealed class EmbeddingResponse { public List<EmbeddingData> Data { get; set; } = new(); }
    private sealed class EmbeddingData { public float[] Embedding { get; set; } = Array.Empty<float>(); }
}
```

Register in `Program.cs`:

```csharp
builder.Services.AddHttpClient<ILlmClient, OpenAiLlmClient>(client =>
{
    client.BaseAddress = new Uri("https://api.openai.com/v1/");
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {builder.Configuration["OpenAI:ApiKey"]}");
});
builder.Services.AddScoped<TriageAgent>();
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test backend/SupportForge.Api.Tests --filter TriageAgentTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add backend/SupportForge.Agents backend/SupportForge.Api
git commit -m "feat: add agent orchestration abstraction, LLM client, and Triage agent"
```

---

### Task 5: Drafter agent + end-to-end query API

**Files:**
- Create: `backend/SupportForge.Agents/DrafterAgent.cs`
- Create: `backend/SupportForge.Agents/AgentPipeline.cs`
- Create: `backend/SupportForge.Api/Controllers/ChatController.cs`
- Create: `backend/SupportForge.Api/Contracts/ChatQueryRequest.cs`
- Create: `backend/SupportForge.Api/Contracts/ChatQueryResponse.cs`
- Test: `backend/SupportForge.Api.Tests/Controllers/ChatControllerTests.cs`

**Interfaces:**
- Consumes: `IAgent`, `AgentContext`, `TriageAgent` (Task 4).
- Produces: `AgentPipeline.RunAsync(AgentContext)` — Task 11's Coordinator replaces this with the full 5-agent pipeline; `POST /api/chat/query` (TSD §6) — the frontend's Query page (Task 15) calls this exact contract.

- [ ] **Step 1: Implement `DrafterAgent`**

`backend/SupportForge.Agents/DrafterAgent.cs`:

```csharp
namespace SupportForge.Agents;

public sealed class DrafterAgent : IAgent
{
    private readonly ILlmClient _llm;
    public string Name => "Drafter";

    public DrafterAgent(ILlmClient llm) => _llm = llm;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        const string systemPrompt = """
            You are a support engineer drafting a reply to a customer.
            Use only the provided KB/code context. If context is empty, say you need more information.
            Respond in Markdown.
            """;

        var userPrompt = $"""
            Customer question: {context.Query}
            Intent: {context.Intent}
            KB context: {string.Join("\n---\n", context.KbSnippets)}
            Code context: {string.Join("\n---\n", context.CodeSnippets)}
            Vision findings: {context.VisionFindings}
            """;

        context.Draft = await _llm.CompleteAsync(systemPrompt, userPrompt, ct);
        context.Confidence = context.KbSnippets.Count + context.CodeSnippets.Count > 0 ? 0.8 : 0.4;
        return context;
    }
}
```

- [ ] **Step 2: Implement the minimal `AgentPipeline`**

`backend/SupportForge.Agents/AgentPipeline.cs`:

```csharp
namespace SupportForge.Agents;

public sealed class AgentPipeline
{
    private readonly IReadOnlyList<IAgent> _agents;

    public AgentPipeline(IEnumerable<IAgent> agents) => _agents = agents.ToList();

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        foreach (var agent in _agents)
            context = await agent.RunAsync(context, ct);

        return context;
    }
}
```

Day 2's pipeline is `[TriageAgent, DrafterAgent]` only — Task 11 swaps this registration to the full 5-agent list once KB/Code/Vision agents exist.

- [ ] **Step 3: Define request/response contracts**

`backend/SupportForge.Api/Contracts/ChatQueryRequest.cs`:

```csharp
namespace SupportForge.Api.Contracts;

public sealed class ChatQueryRequest
{
    public required string ProjectId { get; init; }
    public required string Query { get; init; }
    public string? ScreenshotBase64 { get; init; }
}
```

`backend/SupportForge.Api/Contracts/ChatQueryResponse.cs`:

```csharp
namespace SupportForge.Api.Contracts;

public sealed class ChatQueryResponse
{
    public required string Draft { get; init; }
    public required double Confidence { get; init; }
    public required IReadOnlyList<SourceDto> Sources { get; init; }
}

public sealed record SourceDto(string Label, string Url);
```

- [ ] **Step 4: Write the failing test for `ChatController`**

`backend/SupportForge.Api.Tests/Controllers/ChatControllerTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Moq;
using SupportForge.Agents;
using SupportForge.Api.Controllers;
using SupportForge.Api.Contracts;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class ChatControllerTests
{
    [Fact]
    public async Task Query_ReturnsDraftAndConfidence_FromPipeline()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), default)).ReturnsAsync("code_issue");

        var pipeline = new AgentPipeline(new IAgent[] { new TriageAgent(llm.Object), new DrafterAgent(llm.Object) });
        var controller = new ChatController(pipeline);

        var response = await controller.Query(new ChatQueryRequest { ProjectId = "proj1", Query = "Getting a 500 error" });

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var body = Assert.IsType<ChatQueryResponse>(ok.Value);
        Assert.Equal("code_issue", body.Draft); // DrafterAgent stubs LLM to return same fixed string in this test
    }
}
```

- [ ] **Step 5: Run test to verify it fails**

Run: `dotnet test backend/SupportForge.Api.Tests --filter ChatControllerTests`
Expected: FAIL — `ChatController` does not exist.

- [ ] **Step 6: Implement `ChatController`**

`backend/SupportForge.Api/Controllers/ChatController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using SupportForge.Agents;
using SupportForge.Api.Contracts;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly AgentPipeline _pipeline;

    public ChatController(AgentPipeline pipeline) => _pipeline = pipeline;

    [HttpPost("query")]
    public async Task<ActionResult<ChatQueryResponse>> Query([FromBody] ChatQueryRequest request, CancellationToken ct = default)
    {
        var context = new AgentContext
        {
            ProjectId = request.ProjectId,
            Query = request.Query,
            ScreenshotBase64 = request.ScreenshotBase64,
        };

        var result = await _pipeline.RunAsync(context, ct);

        return Ok(new ChatQueryResponse
        {
            Draft = result.Draft,
            Confidence = result.Confidence,
            Sources = result.Sources.Select(s => new SourceDto(s.Label, s.Url)).ToList(),
        });
    }
}
```

Register in `Program.cs`:

```csharp
builder.Services.AddScoped<DrafterAgent>();
builder.Services.AddScoped<AgentPipeline>(sp => new AgentPipeline(new IAgent[]
{
    sp.GetRequiredService<TriageAgent>(),
    sp.GetRequiredService<DrafterAgent>(),
}));
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test backend/SupportForge.Api.Tests --filter ChatControllerTests`
Expected: PASS.

- [ ] **Step 8: Manual smoke test end-to-end**

Run: `dotnet run --project backend/SupportForge.Api`, then:

```bash
curl -k -X POST https://localhost:5001/api/chat/query -H "Content-Type: application/json" -d "{\"projectId\":\"proj1\",\"query\":\"Getting a 500 error on checkout\"}"
```

Expected: JSON with a `draft`, `confidence`, and empty `sources` array (real OpenAI key required in `appsettings.Development.json` under `OpenAI:ApiKey`).

- [ ] **Step 9: Commit**

```bash
git add backend/SupportForge.Agents backend/SupportForge.Api
git commit -m "feat: add Drafter agent, pipeline runner, and end-to-end chat query API"
```

---

## Day 3 — Ingestion & Tools

### Task 6: Ingestion background worker skeleton + manual trigger endpoint

**Files:**
- Create: `backend/SupportForge.Ingestion/IIngestionJob.cs`
- Create: `backend/SupportForge.Ingestion/IngestionQueue.cs`
- Create: `backend/SupportForge.Ingestion/IngestionBackgroundService.cs`
- Create: `backend/SupportForge.Api/Controllers/IngestionController.cs`
- Test: `backend/SupportForge.Api.Tests/Ingestion/IngestionQueueTests.cs`

**Interfaces:**
- Produces: `IIngestionJob` (`string ProjectId`, `Task RunAsync(CancellationToken ct)`), `IngestionQueue.Enqueue(IIngestionJob)` — Tasks 7 and 8 implement `IIngestionJob` for docs and GitHub; `POST /api/ingestion/trigger` (TSD §6) enqueues jobs looked up from `IProjectRepository`.

- [ ] **Step 1: Write the failing test for `IngestionQueue`**

`backend/SupportForge.Api.Tests/Ingestion/IngestionQueueTests.cs`:

```csharp
using SupportForge.Ingestion;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class IngestionQueueTests
{
    [Fact]
    public async Task Enqueue_Then_DequeueAsync_ReturnsSameJob()
    {
        var queue = new IngestionQueue();
        var ran = false;
        var job = new FakeJob("proj1", () => ran = true);

        queue.Enqueue(job);
        var dequeued = await queue.DequeueAsync(CancellationToken.None);
        await dequeued.RunAsync(CancellationToken.None);

        Assert.Equal("proj1", dequeued.ProjectId);
        Assert.True(ran);
    }

    private sealed class FakeJob : IIngestionJob
    {
        private readonly Action _onRun;
        public FakeJob(string projectId, Action onRun) { ProjectId = projectId; _onRun = onRun; }
        public string ProjectId { get; }
        public Task RunAsync(CancellationToken ct) { _onRun(); return Task.CompletedTask; }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend/SupportForge.Api.Tests --filter IngestionQueueTests`
Expected: FAIL — `IIngestionJob` / `IngestionQueue` do not exist.

- [ ] **Step 3: Implement `IIngestionJob` and `IngestionQueue`**

`backend/SupportForge.Ingestion/IIngestionJob.cs`:

```csharp
namespace SupportForge.Ingestion;

public interface IIngestionJob
{
    string ProjectId { get; }
    Task RunAsync(CancellationToken ct);
}
```

`backend/SupportForge.Ingestion/IngestionQueue.cs`:

```csharp
using System.Threading.Channels;

namespace SupportForge.Ingestion;

public sealed class IngestionQueue
{
    private readonly Channel<IIngestionJob> _channel = Channel.CreateUnbounded<IIngestionJob>();

    public void Enqueue(IIngestionJob job) => _channel.Writer.TryWrite(job);

    public ValueTask<IIngestionJob> DequeueAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}
```

- [ ] **Step 4: Implement `IngestionBackgroundService`**

`backend/SupportForge.Ingestion/IngestionBackgroundService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SupportForge.Ingestion;

public sealed class IngestionBackgroundService : BackgroundService
{
    private readonly IngestionQueue _queue;
    private readonly ILogger<IngestionBackgroundService> _logger;

    public IngestionBackgroundService(IngestionQueue queue, ILogger<IngestionBackgroundService> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await _queue.DequeueAsync(stoppingToken);
            try
            {
                await job.RunAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ingestion job failed for project {ProjectId}", job.ProjectId);
            }
        }
    }
}
```

- [ ] **Step 5: Implement `POST /api/ingestion/trigger`**

`backend/SupportForge.Api/Controllers/IngestionController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.Ingestion;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/ingestion")]
public class IngestionController : ControllerBase
{
    private readonly IngestionQueue _queue;
    private readonly IProjectRepository _projects;
    private readonly IServiceProvider _services;

    public IngestionController(IngestionQueue queue, IProjectRepository projects, IServiceProvider services)
    {
        _queue = queue;
        _projects = projects;
        _services = services;
    }

    public sealed record TriggerRequest(string ProjectId);

    [HttpPost("trigger")]
    public async Task<IActionResult> Trigger([FromBody] TriggerRequest request, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(request.ProjectId, ct);
        if (project is null) return NotFound();

        // Task 7 / Task 8 register the concrete job factories that read `project.KbSources` / `project.Repos`.
        foreach (var factory in _services.GetServices<IIngestionJobFactory>())
            foreach (var job in factory.CreateJobs(project))
                _queue.Enqueue(job);

        return Accepted();
    }
}
```

Add the factory interface so Tasks 7/8 can plug in without touching this controller:

`backend/SupportForge.Ingestion/IIngestionJob.cs` (append):

```csharp
public interface IIngestionJobFactory
{
    IEnumerable<IIngestionJob> CreateJobs(SupportForge.Core.Entities.Project project);
}
```

Register in `Program.cs`: `builder.Services.AddSingleton<IngestionQueue>(); builder.Services.AddHostedService<IngestionBackgroundService>();`

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test backend/SupportForge.Api.Tests --filter IngestionQueueTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add backend/SupportForge.Ingestion backend/SupportForge.Api
git commit -m "feat: add ingestion background worker, queue, and manual trigger endpoint"
```

---

### Task 7: Document KB connector (chunk → embed → store)

**Files:**
- Create: `backend/SupportForge.Ingestion/Documents/DocumentChunker.cs`
- Create: `backend/SupportForge.Ingestion/Documents/DocumentIngestionJob.cs`
- Create: `backend/SupportForge.Ingestion/Documents/DocumentIngestionJobFactory.cs`
- Test: `backend/SupportForge.Api.Tests/Ingestion/DocumentChunkerTests.cs`

**Interfaces:**
- Consumes: `IIngestionJob`, `IIngestionJobFactory` (Task 6), `ILlmClient.EmbedAsync` (Task 4), `IVectorStoreService.UpsertAsync` (Task 2).
- Produces: `{projectId}-kb` Chroma collection populated from `.md`/`.txt` files — consumed by the KB Search tool (Task 9).

- [ ] **Step 1: Write the failing test for `DocumentChunker`**

`backend/SupportForge.Api.Tests/Ingestion/DocumentChunkerTests.cs`:

```csharp
using SupportForge.Ingestion.Documents;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class DocumentChunkerTests
{
    [Fact]
    public void Chunk_SplitsLongText_IntoChunksUnderMaxSize()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 500)); // ~2500 chars
        var chunks = DocumentChunker.Chunk(text, maxChars: 500);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 500));
        Assert.Equal(text, string.Join(" ", chunks).Replace("  ", " ").Trim());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend/SupportForge.Api.Tests --filter DocumentChunkerTests`
Expected: FAIL — `DocumentChunker` does not exist.

- [ ] **Step 3: Implement `DocumentChunker`**

`backend/SupportForge.Ingestion/Documents/DocumentChunker.cs`:

```csharp
namespace SupportForge.Ingestion.Documents;

public static class DocumentChunker
{
    public static List<string> Chunk(string text, int maxChars = 1500)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        var current = new List<string>();
        var currentLength = 0;

        foreach (var word in words)
        {
            if (currentLength + word.Length + 1 > maxChars && current.Count > 0)
            {
                chunks.Add(string.Join(' ', current));
                current.Clear();
                currentLength = 0;
            }

            current.Add(word);
            currentLength += word.Length + 1;
        }

        if (current.Count > 0) chunks.Add(string.Join(' ', current));
        return chunks;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend/SupportForge.Api.Tests --filter DocumentChunkerTests`
Expected: PASS.

- [ ] **Step 5: Implement `DocumentIngestionJob` and its factory**

`backend/SupportForge.Ingestion/Documents/DocumentIngestionJob.cs`:

```csharp
using SupportForge.Agents;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;

namespace SupportForge.Ingestion.Documents;

public sealed class DocumentIngestionJob : IIngestionJob
{
    private readonly string _folderPath;
    private readonly ILlmClient _llm;
    private readonly IVectorStoreService _vectorStore;

    public string ProjectId { get; }

    public DocumentIngestionJob(string projectId, string folderPath, ILlmClient llm, IVectorStoreService vectorStore)
    {
        ProjectId = projectId;
        _folderPath = folderPath;
        _llm = llm;
        _vectorStore = vectorStore;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_folderPath)) return;

        var files = Directory.EnumerateFiles(_folderPath, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".md") || f.EndsWith(".txt"));

        var documents = new List<VectorDocument>();
        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file, ct);
            var chunks = DocumentChunker.Chunk(text);

            for (var i = 0; i < chunks.Count; i++)
            {
                var embedding = await _llm.EmbedAsync(chunks[i], ct);
                documents.Add(new VectorDocument(
                    Id: $"{Path.GetFileNameWithoutExtension(file)}-{i}",
                    Text: chunks[i],
                    Embedding: embedding,
                    Metadata: new Dictionary<string, string> { ["source"] = file, ["chunk"] = i.ToString() }));
            }
        }

        if (documents.Count > 0)
            await _vectorStore.UpsertAsync($"{ProjectId}-kb", documents, ct);
    }
}
```

`backend/SupportForge.Ingestion/Documents/DocumentIngestionJobFactory.cs`:

```csharp
using SupportForge.Agents;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;

namespace SupportForge.Ingestion.Documents;

public sealed class DocumentIngestionJobFactory : IIngestionJobFactory
{
    private readonly ILlmClient _llm;
    private readonly IVectorStoreService _vectorStore;

    public DocumentIngestionJobFactory(ILlmClient llm, IVectorStoreService vectorStore)
    {
        _llm = llm;
        _vectorStore = vectorStore;
    }

    public IEnumerable<IIngestionJob> CreateJobs(Project project) =>
        project.KbSources
            .Where(s => s.Type == KbSourceType.Documents)
            .Select(s => new DocumentIngestionJob(project.Id, s.Location, _llm, _vectorStore));
}
```

Register in `Program.cs`: `builder.Services.AddSingleton<IIngestionJobFactory, DocumentIngestionJobFactory>();`

- [ ] **Step 6: Commit**

```bash
git add backend/SupportForge.Ingestion
git commit -m "feat: add document KB connector with chunking, embedding, and vector upsert"
```

---

### Task 8: GitHub code ingestion (LibGit2Sharp)

**Files:**
- Create: `backend/SupportForge.Ingestion/Code/GitRepoSyncService.cs`
- Create: `backend/SupportForge.Ingestion/Code/CodeIngestionJob.cs`
- Create: `backend/SupportForge.Ingestion/Code/CodeIngestionJobFactory.cs`
- Test: `backend/SupportForge.Api.Tests/Ingestion/GitRepoSyncServiceTests.cs`

**Interfaces:**
- Consumes: `IIngestionJob`, `IIngestionJobFactory` (Task 6), `ILlmClient.EmbedAsync` (Task 4), `IVectorStoreService.UpsertAsync` (Task 2), `DocumentChunker.Chunk` (Task 7, reused for code files).
- Produces: `{projectId}-code` Chroma collection — consumed by the Code Search tool (Task 9).

- [ ] **Step 1: Add LibGit2Sharp package**

```bash
dotnet add backend/SupportForge.Ingestion package LibGit2Sharp
```

- [ ] **Step 2: Write the failing test for `GitRepoSyncService`**

`backend/SupportForge.Api.Tests/Ingestion/GitRepoSyncServiceTests.cs` — uses a real local bare repo as the "remote" so the test has no network dependency:

```csharp
using LibGit2Sharp;
using SupportForge.Ingestion.Code;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class GitRepoSyncServiceTests
{
    [Fact]
    public void CloneOrPull_ClonesFreshRepo_WhenLocalPathDoesNotExist()
    {
        var remoteDir = Path.Combine(Path.GetTempPath(), "remote-" + Guid.NewGuid());
        var localDir = Path.Combine(Path.GetTempPath(), "local-" + Guid.NewGuid());
        Repository.Init(remoteDir, isBare: true);

        var seedDir = Path.Combine(Path.GetTempPath(), "seed-" + Guid.NewGuid());
        Repository.Clone(remoteDir, seedDir);
        File.WriteAllText(Path.Combine(seedDir, "readme.md"), "hello");
        using (var seedRepo = new Repository(seedDir))
        {
            Commands.Stage(seedRepo, "*");
            var sig = new Signature("test", "test@test.com", DateTimeOffset.Now);
            seedRepo.Commit("initial commit", sig, sig);
            seedRepo.Network.Push(seedRepo.Branches["master"]);
        }

        var sut = new GitRepoSyncService();
        sut.CloneOrPull(remoteDir, localDir, "master");

        Assert.True(File.Exists(Path.Combine(localDir, "readme.md")));

        Directory.Delete(remoteDir, recursive: true);
        Directory.Delete(localDir, recursive: true);
        Directory.Delete(seedDir, recursive: true);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test backend/SupportForge.Api.Tests --filter GitRepoSyncServiceTests`
Expected: FAIL — `GitRepoSyncService` does not exist.

- [ ] **Step 4: Implement `GitRepoSyncService`**

`backend/SupportForge.Ingestion/Code/GitRepoSyncService.cs`:

```csharp
using LibGit2Sharp;

namespace SupportForge.Ingestion.Code;

public sealed class GitRepoSyncService
{
    public void CloneOrPull(string remoteUrl, string localPath, string branch)
    {
        if (!Directory.Exists(Path.Combine(localPath, ".git")))
        {
            Repository.Clone(remoteUrl, localPath, new CloneOptions { BranchName = branch });
            return;
        }

        using var repo = new Repository(localPath);
        Commands.Pull(repo, new Signature("supportforge", "supportforge@internal", DateTimeOffset.Now), new PullOptions());
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test backend/SupportForge.Api.Tests --filter GitRepoSyncServiceTests`
Expected: PASS.

- [ ] **Step 6: Implement `CodeIngestionJob` and its factory**

`backend/SupportForge.Ingestion/Code/CodeIngestionJob.cs`:

```csharp
using SupportForge.Agents;
using SupportForge.Ingestion.Documents;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;

namespace SupportForge.Ingestion.Code;

public sealed class CodeIngestionJob : IIngestionJob
{
    private static readonly string[] CodeExtensions = { ".cs", ".ts", ".tsx", ".py", ".java", ".go" };

    private readonly string _repoUrl;
    private readonly string _branch;
    private readonly string _localCachePath;
    private readonly GitRepoSyncService _gitSync;
    private readonly ILlmClient _llm;
    private readonly IVectorStoreService _vectorStore;

    public string ProjectId { get; }

    public CodeIngestionJob(string projectId, string repoUrl, string branch, string localCachePath,
        GitRepoSyncService gitSync, ILlmClient llm, IVectorStoreService vectorStore)
    {
        ProjectId = projectId;
        _repoUrl = repoUrl;
        _branch = branch;
        _localCachePath = localCachePath;
        _gitSync = gitSync;
        _llm = llm;
        _vectorStore = vectorStore;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _gitSync.CloneOrPull(_repoUrl, _localCachePath, _branch);

        var files = Directory.EnumerateFiles(_localCachePath, "*.*", SearchOption.AllDirectories)
            .Where(f => CodeExtensions.Contains(Path.GetExtension(f)) && !f.Contains(".git"));

        var documents = new List<VectorDocument>();
        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file, ct);
            var relativePath = Path.GetRelativePath(_localCachePath, file);
            var chunks = DocumentChunker.Chunk(text, maxChars: 2000);

            for (var i = 0; i < chunks.Count; i++)
            {
                var embedding = await _llm.EmbedAsync(chunks[i], ct);
                documents.Add(new VectorDocument(
                    Id: $"{relativePath.Replace(Path.DirectorySeparatorChar, '_')}-{i}",
                    Text: chunks[i],
                    Embedding: embedding,
                    Metadata: new Dictionary<string, string> { ["file"] = relativePath, ["chunk"] = i.ToString() }));
            }
        }

        if (documents.Count > 0)
            await _vectorStore.UpsertAsync($"{ProjectId}-code", documents, ct);
    }
}
```

`backend/SupportForge.Ingestion/Code/CodeIngestionJobFactory.cs`:

```csharp
using SupportForge.Agents;
using SupportForge.Core.Entities;
using SupportForge.VectorStore;

namespace SupportForge.Ingestion.Code;

public sealed class CodeIngestionJobFactory : IIngestionJobFactory
{
    private readonly GitRepoSyncService _gitSync;
    private readonly ILlmClient _llm;
    private readonly IVectorStoreService _vectorStore;
    private readonly string _cacheRoot;

    public CodeIngestionJobFactory(GitRepoSyncService gitSync, ILlmClient llm, IVectorStoreService vectorStore, string cacheRoot)
    {
        _gitSync = gitSync;
        _llm = llm;
        _vectorStore = vectorStore;
        _cacheRoot = cacheRoot;
    }

    public IEnumerable<IIngestionJob> CreateJobs(Project project) =>
        project.Repos.Select(r => new CodeIngestionJob(
            project.Id,
            $"https://github.com/{r.Owner}/{r.Repo}.git",
            r.DefaultBranch,
            Path.Combine(_cacheRoot, project.Id, r.Repo),
            _gitSync, _llm, _vectorStore));
}
```

Register in `Program.cs`:

```csharp
builder.Services.AddSingleton<GitRepoSyncService>();
builder.Services.AddSingleton<IIngestionJobFactory>(sp => new CodeIngestionJobFactory(
    sp.GetRequiredService<GitRepoSyncService>(),
    sp.GetRequiredService<ILlmClient>(),
    sp.GetRequiredService<IVectorStoreService>(),
    Path.Combine(builder.Environment.ContentRootPath, "App_Data", "repos")));
```

- [ ] **Step 7: Commit**

```bash
git add backend/SupportForge.Ingestion
git commit -m "feat: add GitHub code ingestion via LibGit2Sharp with chunk/embed/upsert"
```

---

### Task 9: KB Search tool + Code Search tool (agent-callable)

**Files:**
- Create: `backend/SupportForge.Agents/Tools/KbSearchTool.cs`
- Create: `backend/SupportForge.Agents/Tools/CodeSearchTool.cs`
- Create: `backend/SupportForge.Agents/KbResearcherAgent.cs`
- Create: `backend/SupportForge.Agents/CodeAnalyzerAgent.cs`
- Test: `backend/SupportForge.Api.Tests/Agents/KbResearcherAgentTests.cs`
- Test: `backend/SupportForge.Api.Tests/Agents/CodeAnalyzerAgentTests.cs`

**Interfaces:**
- Consumes: `IVectorStoreService.QueryAsync` (Task 2), `ILlmClient.EmbedAsync` (Task 4), `AgentContext.KbSnippets` / `CodeSnippets` / `Sources` (Task 4).
- Produces: `KbResearcherAgent`, `CodeAnalyzerAgent` (both `IAgent`) — Task 11's Coordinator inserts these into the pipeline between Triage and Drafter.

- [ ] **Step 1: Implement `KbSearchTool`**

`backend/SupportForge.Agents/Tools/KbSearchTool.cs`:

```csharp
using SupportForge.VectorStore;

namespace SupportForge.Agents.Tools;

public sealed class KbSearchTool
{
    private readonly ILlmClient _llm;
    private readonly IVectorStoreService _vectorStore;

    public KbSearchTool(ILlmClient llm, IVectorStoreService vectorStore)
    {
        _llm = llm;
        _vectorStore = vectorStore;
    }

    public async Task<IReadOnlyList<(string Text, string Source)>> SearchAsync(string projectId, string query, int topK = 5, CancellationToken ct = default)
    {
        var embedding = await _llm.EmbedAsync(query, ct);
        var results = await _vectorStore.QueryAsync($"{projectId}-kb", embedding, topK, ct: ct);
        return results.Select(r => (r.Text, r.Metadata.GetValueOrDefault("source", "unknown"))).ToList();
    }
}
```

- [ ] **Step 2: Implement `CodeSearchTool`**

`backend/SupportForge.Agents/Tools/CodeSearchTool.cs`:

```csharp
using SupportForge.VectorStore;

namespace SupportForge.Agents.Tools;

public sealed class CodeSearchTool
{
    private readonly ILlmClient _llm;
    private readonly IVectorStoreService _vectorStore;

    public CodeSearchTool(ILlmClient llm, IVectorStoreService vectorStore)
    {
        _llm = llm;
        _vectorStore = vectorStore;
    }

    public async Task<IReadOnlyList<(string Text, string File)>> SearchAsync(string projectId, string query, int topK = 5, CancellationToken ct = default)
    {
        var embedding = await _llm.EmbedAsync(query, ct);
        var results = await _vectorStore.QueryAsync($"{projectId}-code", embedding, topK, ct: ct);
        return results.Select(r => (r.Text, r.Metadata.GetValueOrDefault("file", "unknown"))).ToList();
    }
}
```

- [ ] **Step 3: Write the failing test for `KbResearcherAgent`**

`backend/SupportForge.Api.Tests/Agents/KbResearcherAgentTests.cs`:

```csharp
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class KbResearcherAgentTests
{
    [Fact]
    public async Task RunAsync_PopulatesKbSnippetsAndSources()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), default)).ReturnsAsync(new float[] { 0.1f });

        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore.Setup(v => v.QueryAsync("proj1-kb", It.IsAny<float[]>(), 5, null, default))
            .ReturnsAsync(new List<VectorQueryResult> { new("doc-1", "reset password steps", 0.1f, new Dictionary<string, string> { ["source"] = "kb/reset.md" }) });

        var tool = new KbSearchTool(llm.Object, vectorStore.Object);
        var agent = new KbResearcherAgent(tool);
        var context = new AgentContext { ProjectId = "proj1", Query = "how do I reset my password" };

        var result = await agent.RunAsync(context);

        Assert.Single(result.KbSnippets);
        Assert.Contains(result.Sources, s => s.Url == "kb/reset.md");
    }
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test backend/SupportForge.Api.Tests --filter KbResearcherAgentTests`
Expected: FAIL — `KbResearcherAgent` does not exist.

- [ ] **Step 5: Implement `KbResearcherAgent` and `CodeAnalyzerAgent`**

`backend/SupportForge.Agents/KbResearcherAgent.cs`:

```csharp
using SupportForge.Agents.Tools;

namespace SupportForge.Agents;

public sealed class KbResearcherAgent : IAgent
{
    private readonly KbSearchTool _tool;
    public string Name => "KbResearcher";

    public KbResearcherAgent(KbSearchTool tool) => _tool = tool;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        var results = await _tool.SearchAsync(context.ProjectId, context.Query, ct: ct);
        foreach (var (text, source) in results)
        {
            context.KbSnippets.Add(text);
            context.Sources.Add(($"KB: {Path.GetFileName(source)}", source));
        }
        return context;
    }
}
```

`backend/SupportForge.Agents/CodeAnalyzerAgent.cs`:

```csharp
using SupportForge.Agents.Tools;

namespace SupportForge.Agents;

public sealed class CodeAnalyzerAgent : IAgent
{
    private readonly CodeSearchTool _tool;
    public string Name => "CodeAnalyzer";

    public CodeAnalyzerAgent(CodeSearchTool tool) => _tool = tool;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        if (context.Intent != "code_issue") return context;

        var results = await _tool.SearchAsync(context.ProjectId, context.Query, ct: ct);
        foreach (var (text, file) in results)
        {
            context.CodeSnippets.Add(text);
            context.Sources.Add(($"Code: {file}", file));
        }
        return context;
    }
}
```

- [ ] **Step 6: Write the failing test for `CodeAnalyzerAgent`, then implement (steps mirror Step 3–5)**

`backend/SupportForge.Api.Tests/Agents/CodeAnalyzerAgentTests.cs`:

```csharp
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class CodeAnalyzerAgentTests
{
    [Fact]
    public async Task RunAsync_SkipsSearch_WhenIntentIsNotCodeIssue()
    {
        var llm = new Mock<ILlmClient>();
        var vectorStore = new Mock<IVectorStoreService>(MockBehavior.Strict);
        var agent = new CodeAnalyzerAgent(new CodeSearchTool(llm.Object, vectorStore.Object));

        var context = new AgentContext { ProjectId = "proj1", Query = "what is my account balance", Intent = "kb_question" };
        var result = await agent.RunAsync(context);

        Assert.Empty(result.CodeSnippets);
        vectorStore.VerifyNoOtherCalls();
    }
}
```

Run: `dotnet test backend/SupportForge.Api.Tests --filter "KbResearcherAgentTests|CodeAnalyzerAgentTests"`
Expected: PASS for both after the implementations above are in place.

- [ ] **Step 7: Commit**

```bash
git add backend/SupportForge.Agents
git commit -m "feat: add KB/Code search tools and KbResearcher/CodeAnalyzer agents"
```

---

## Day 4 — Vision + Advanced Agents

### Task 10: Vision analyzer tool

**Files:**
- Create: `backend/SupportForge.Agents/Tools/VisionAnalysisTool.cs`
- Create: `backend/SupportForge.Agents/VisionAnalyzerAgent.cs`
- Test: `backend/SupportForge.Api.Tests/Agents/VisionAnalyzerAgentTests.cs`

**Interfaces:**
- Consumes: `ILlmClient` (Task 4) — extended below with `AnalyzeImageAsync`.
- Produces: `VisionAnalyzerAgent : IAgent` setting `AgentContext.VisionFindings` — Task 11's Coordinator runs this only when `ScreenshotBase64` is present.

- [ ] **Step 1: Extend `ILlmClient` with image analysis**

`backend/SupportForge.Agents/ILlmClient.cs` (add method to the interface):

```csharp
Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default);
```

`backend/SupportForge.Agents/OpenAiLlmClient.cs` (add implementation):

```csharp
public async Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default)
{
    var response = await _http.PostAsJsonAsync("chat/completions", new
    {
        model = "gpt-4o-mini",
        messages = new object[]
        {
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = prompt },
                    new { type = "image_url", image_url = new { url = $"data:image/png;base64,{base64Image}" } },
                },
            },
        },
    }, ct);
    response.EnsureSuccessStatusCode();

    var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
    return body?.Choices.FirstOrDefault()?.Message.Content?.Trim() ?? string.Empty;
}
```

- [ ] **Step 2: Implement `VisionAnalysisTool`**

`backend/SupportForge.Agents/Tools/VisionAnalysisTool.cs`:

```csharp
namespace SupportForge.Agents.Tools;

public sealed class VisionAnalysisTool
{
    private readonly ILlmClient _llm;

    public VisionAnalysisTool(ILlmClient llm) => _llm = llm;

    public Task<string> AnalyzeAsync(string base64Image, CancellationToken ct = default) =>
        _llm.AnalyzeImageAsync(base64Image,
            "Describe any error messages, stack traces, or UI state visible in this screenshot relevant to a support ticket.",
            ct);
}
```

- [ ] **Step 3: Write the failing test for `VisionAnalyzerAgent`**

`backend/SupportForge.Api.Tests/Agents/VisionAnalyzerAgentTests.cs`:

```csharp
using Moq;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class VisionAnalyzerAgentTests
{
    [Fact]
    public async Task RunAsync_SetsVisionFindings_WhenScreenshotPresent()
    {
        var llm = new Mock<ILlmClient>();
        llm.Setup(l => l.AnalyzeImageAsync(It.IsAny<string>(), It.IsAny<string>(), default))
           .ReturnsAsync("NullReferenceException at CheckoutController.cs:42");

        var agent = new VisionAnalyzerAgent(new VisionAnalysisTool(llm.Object));
        var context = new AgentContext { ProjectId = "proj1", Query = "why does checkout fail", ScreenshotBase64 = "base64data" };

        var result = await agent.RunAsync(context);

        Assert.Equal("NullReferenceException at CheckoutController.cs:42", result.VisionFindings);
    }

    [Fact]
    public async Task RunAsync_SkipsAnalysis_WhenNoScreenshot()
    {
        var llm = new Mock<ILlmClient>(MockBehavior.Strict);
        var agent = new VisionAnalyzerAgent(new VisionAnalysisTool(llm.Object));
        var context = new AgentContext { ProjectId = "proj1", Query = "why does checkout fail" };

        var result = await agent.RunAsync(context);

        Assert.Null(result.VisionFindings);
        llm.VerifyNoOtherCalls();
    }
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test backend/SupportForge.Api.Tests --filter VisionAnalyzerAgentTests`
Expected: FAIL — `VisionAnalyzerAgent` does not exist.

- [ ] **Step 5: Implement `VisionAnalyzerAgent`**

`backend/SupportForge.Agents/VisionAnalyzerAgent.cs`:

```csharp
using SupportForge.Agents.Tools;

namespace SupportForge.Agents;

public sealed class VisionAnalyzerAgent : IAgent
{
    private readonly VisionAnalysisTool _tool;
    public string Name => "VisionAnalyzer";

    public VisionAnalyzerAgent(VisionAnalysisTool tool) => _tool = tool;

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(context.ScreenshotBase64)) return context;

        context.VisionFindings = await _tool.AnalyzeAsync(context.ScreenshotBase64, ct);
        return context;
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test backend/SupportForge.Api.Tests --filter VisionAnalyzerAgentTests`
Expected: PASS (2 tests).

- [ ] **Step 7: Commit**

```bash
git add backend/SupportForge.Agents
git commit -m "feat: add vision analysis tool and VisionAnalyzer agent"
```

---

### Task 11: Full multi-agent workflow (Coordinator)

**Files:**
- Create: `backend/SupportForge.Agents/CoordinatorPipeline.cs`
- Modify: `backend/SupportForge.Api/Program.cs` (swap `AgentPipeline` registration)
- Test: `backend/SupportForge.Api.Tests/Agents/CoordinatorPipelineTests.cs`

**Interfaces:**
- Consumes: `TriageAgent`, `KbResearcherAgent`, `CodeAnalyzerAgent`, `VisionAnalyzerAgent`, `DrafterAgent` (Tasks 4, 5, 9, 10).
- Produces: `CoordinatorPipeline.RunAsync(AgentContext)` — replaces `AgentPipeline` as what `ChatController` (Task 5) calls; same public shape (`Task<AgentContext> RunAsync(AgentContext, CancellationToken)`) so the controller needs no changes beyond the DI registration.

- [ ] **Step 1: Write the failing test for ordering/conditional execution**

`backend/SupportForge.Api.Tests/Agents/CoordinatorPipelineTests.cs`:

```csharp
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class CoordinatorPipelineTests
{
    [Fact]
    public async Task RunAsync_RunsAgentsInOrder_AndRecordsExecutionOrder()
    {
        var order = new List<string>();
        var agents = new IAgent[]
        {
            new RecordingAgent("Triage", order),
            new RecordingAgent("KbResearcher", order),
            new RecordingAgent("CodeAnalyzer", order),
            new RecordingAgent("VisionAnalyzer", order),
            new RecordingAgent("Drafter", order),
        };

        var pipeline = new CoordinatorPipeline(agents);
        await pipeline.RunAsync(new AgentContext { ProjectId = "proj1", Query = "test" });

        Assert.Equal(new[] { "Triage", "KbResearcher", "CodeAnalyzer", "VisionAnalyzer", "Drafter" }, order);
    }

    private sealed class RecordingAgent : IAgent
    {
        private readonly List<string> _order;
        public string Name { get; }
        public RecordingAgent(string name, List<string> order) { Name = name; _order = order; }
        public Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
        {
            _order.Add(Name);
            return Task.FromResult(context);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend/SupportForge.Api.Tests --filter CoordinatorPipelineTests`
Expected: FAIL — `CoordinatorPipeline` does not exist.

- [ ] **Step 3: Implement `CoordinatorPipeline`**

`backend/SupportForge.Agents/CoordinatorPipeline.cs`:

```csharp
namespace SupportForge.Agents;

public sealed class CoordinatorPipeline
{
    private readonly IReadOnlyList<IAgent> _agents;

    public CoordinatorPipeline(IEnumerable<IAgent> agents) => _agents = agents.ToList();

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        foreach (var agent in _agents)
            context = await agent.RunAsync(context, ct);

        return context;
    }
}
```

This is deliberately identical in shape to `AgentPipeline` from Task 5 — the only thing that changes is which agents get registered. Keeping both types (rather than deleting `AgentPipeline`) documents the Day 2 → Day 4 evolution; delete `AgentPipeline` in this step since nothing else references it after the swap.

- [ ] **Step 4: Delete `AgentPipeline` and update `ChatController` + DI registration**

```bash
rm backend/SupportForge.Agents/AgentPipeline.cs
```

`backend/SupportForge.Api/Controllers/ChatController.cs` — change constructor/field type from `AgentPipeline` to `CoordinatorPipeline` (same method call `_pipeline.RunAsync(context, ct)`).

`backend/SupportForge.Api/Program.cs` — replace the Task 5 registration:

```csharp
builder.Services.AddScoped<KbResearcherAgent>();
builder.Services.AddScoped<CodeAnalyzerAgent>();
builder.Services.AddScoped<VisionAnalyzerAgent>();
builder.Services.AddScoped<CoordinatorPipeline>(sp => new CoordinatorPipeline(new IAgent[]
{
    sp.GetRequiredService<TriageAgent>(),
    sp.GetRequiredService<KbResearcherAgent>(),
    sp.GetRequiredService<CodeAnalyzerAgent>(),
    sp.GetRequiredService<VisionAnalyzerAgent>(),
    sp.GetRequiredService<DrafterAgent>(),
}));
```

Also register the tools used by these agents (if not already): `builder.Services.AddScoped<KbSearchTool>(); builder.Services.AddScoped<CodeSearchTool>(); builder.Services.AddScoped<VisionAnalysisTool>();`

- [ ] **Step 5: Run test to verify it passes, and re-run the full suite**

Run: `dotnet test backend/SupportForge.Api.Tests --filter CoordinatorPipelineTests`
Expected: PASS.

Run: `dotnet test backend/SupportForge.Api.Tests`
Expected: all tests pass (existing `ChatControllerTests` needs its `AgentPipeline` reference updated to `CoordinatorPipeline` — same constructor arg order).

- [ ] **Step 6: Manual smoke test with a real query end-to-end**

Run: `dotnet run --project backend/SupportForge.Api`, POST to `/api/chat/query` with a `projectId` that has ingested KB/code data (Tasks 7–8 must have run via `/api/ingestion/trigger` first). Confirm the response includes non-empty `sources`.

- [ ] **Step 7: Commit**

```bash
git add backend/SupportForge.Agents backend/SupportForge.Api backend/SupportForge.Api.Tests
git commit -m "feat: wire full 5-agent Coordinator pipeline into chat query endpoint"
```

---

### Task 12: Freshness metadata

**Files:**
- Modify: `backend/SupportForge.Core/Entities/KbSourceConfig.cs` (already has `LastSyncedAt`)
- Create: `backend/SupportForge.Core/FreshnessCalculator.cs`
- Modify: `backend/SupportForge.Ingestion/Documents/DocumentIngestionJob.cs` (record sync time)
- Modify: `backend/SupportForge.Ingestion/Code/CodeIngestionJob.cs` (record sync time)
- Create: `backend/SupportForge.Api/Controllers/FreshnessController.cs`
- Test: `backend/SupportForge.Api.Tests/FreshnessCalculatorTests.cs`

**Interfaces:**
- Consumes: `IProjectRepository` (Task 3), `Project.KbSources` (Task 3).
- Produces: `FreshnessCalculator.Calculate(Project)` returning a `FreshnessScore` — consumed by the Dashboard page (Task 14) via `GET /api/projects/{id}/freshness`.

- [ ] **Step 1: Write the failing test for `FreshnessCalculator`**

`backend/SupportForge.Api.Tests/FreshnessCalculatorTests.cs`:

```csharp
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests;

public class FreshnessCalculatorTests
{
    [Fact]
    public void Calculate_ReturnsStale_WhenAnySourceOlderThan7Days()
    {
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            KbSources = new List<KbSourceConfig>
            {
                new(KbSourceType.Documents, "docs/", DateTimeOffset.UtcNow.AddDays(-10)),
            },
        };

        var result = FreshnessCalculator.Calculate(project);

        Assert.False(result.IsFresh);
        Assert.Equal("docs/", result.StaleSources.Single());
    }

    [Fact]
    public void Calculate_ReturnsFresh_WhenAllSourcesWithin7Days()
    {
        var project = new Project
        {
            Id = "proj1",
            Name = "Test",
            KbSources = new List<KbSourceConfig> { new(KbSourceType.Documents, "docs/", DateTimeOffset.UtcNow.AddDays(-1)) },
        };

        var result = FreshnessCalculator.Calculate(project);

        Assert.True(result.IsFresh);
        Assert.Empty(result.StaleSources);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend/SupportForge.Api.Tests --filter FreshnessCalculatorTests`
Expected: FAIL — `FreshnessCalculator` does not exist.

- [ ] **Step 3: Implement `FreshnessCalculator`**

`backend/SupportForge.Core/FreshnessCalculator.cs`:

```csharp
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed record FreshnessScore(bool IsFresh, IReadOnlyList<string> StaleSources);

public static class FreshnessCalculator
{
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(7);

    public static FreshnessScore Calculate(Project project)
    {
        var now = DateTimeOffset.UtcNow;
        var stale = project.KbSources
            .Where(s => s.LastSyncedAt is null || now - s.LastSyncedAt.Value > StaleThreshold)
            .Select(s => s.Location)
            .ToList();

        return new FreshnessScore(stale.Count == 0, stale);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend/SupportForge.Api.Tests --filter FreshnessCalculatorTests`
Expected: PASS.

- [ ] **Step 5: Record sync timestamps in ingestion jobs**

`backend/SupportForge.Ingestion/Documents/DocumentIngestionJob.cs` — inject `IProjectRepository` and, at the end of `RunAsync` after the upsert, update the matching `KbSourceConfig.LastSyncedAt` via `_projects.GetByIdAsync` → mutate → `UpsertAsync`. (Constructor gains an `IProjectRepository projects` parameter; `DocumentIngestionJobFactory` passes it through from its own injected `IProjectRepository`.) Apply the same pattern to `CodeIngestionJob`/`CodeIngestionJobFactory` for `GitHubRepoConfig` — add a `LastSyncedAt` field to `GitHubRepoConfig` alongside the existing fields.

- [ ] **Step 6: Add `GET /api/projects/{id}/freshness`**

`backend/SupportForge.Api/Controllers/FreshnessController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/projects/{projectId}/freshness")]
public class FreshnessController : ControllerBase
{
    private readonly IProjectRepository _repo;

    public FreshnessController(IProjectRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<IActionResult> Get(string projectId, CancellationToken ct)
    {
        var project = await _repo.GetByIdAsync(projectId, ct);
        if (project is null) return NotFound();

        return Ok(FreshnessCalculator.Calculate(project));
    }
}
```

- [ ] **Step 7: Commit**

```bash
git add backend/SupportForge.Core backend/SupportForge.Ingestion backend/SupportForge.Api
git commit -m "feat: add freshness scoring, sync timestamp tracking, and freshness endpoint"
```

---

## Day 5 — React UI: Core Query Flow

### Task 13: App shell, routing, API client, state store

**Files:**
- Create: `frontend/src/lib/apiClient.ts`
- Create: `frontend/src/lib/queryClient.ts`
- Create: `frontend/src/store/useAppStore.ts`
- Create: `frontend/src/App.tsx` (replace default)
- Create: `frontend/src/main.tsx` (replace default)
- Create: `frontend/src/pages/DashboardPage.tsx` (stub, filled in Task 14)
- Create: `frontend/src/pages/QueryPage.tsx` (stub, filled in Task 15)
- Test: `frontend/src/lib/apiClient.test.ts`

**Interfaces:**
- Produces: `apiClient` (Axios instance), `useAppStore` (Zustand: `selectedProjectId`, `setSelectedProjectId`, `theme`, `toggleTheme`) — consumed by every page built in Tasks 14–18.

- [ ] **Step 1: Install router and test tooling**

```bash
cd frontend
npm install react-router-dom
npm install -D vitest @testing-library/react @testing-library/jest-dom jsdom
```

Add to `frontend/vite.config.ts`:

```ts
export default defineConfig({
  plugins: [react()],
  test: { environment: 'jsdom', globals: true },
});
```

- [ ] **Step 2: Write the failing test for `apiClient`**

`frontend/src/lib/apiClient.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { apiClient } from './apiClient';

describe('apiClient', () => {
  it('has the API base URL configured', () => {
    expect(apiClient.defaults.baseURL).toBe('https://localhost:5001/api');
  });
});
```

- [ ] **Step 3: Run test to verify it fails**

Run: `npx vitest run src/lib/apiClient.test.ts`
Expected: FAIL — `apiClient.ts` does not exist.

- [ ] **Step 4: Implement `apiClient`, `queryClient`, `useAppStore`**

`frontend/src/lib/apiClient.ts`:

```ts
import axios from 'axios';

export const apiClient = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL ?? 'https://localhost:5001/api',
});
```

`frontend/src/lib/queryClient.ts`:

```ts
import { QueryClient } from '@tanstack/react-query';

export const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, staleTime: 30_000 } },
});
```

`frontend/src/store/useAppStore.ts`:

```ts
import { create } from 'zustand';

type Theme = 'light' | 'dark';

interface AppState {
  selectedProjectId: string | null;
  setSelectedProjectId: (id: string) => void;
  theme: Theme;
  toggleTheme: () => void;
}

export const useAppStore = create<AppState>((set) => ({
  selectedProjectId: null,
  setSelectedProjectId: (id) => set({ selectedProjectId: id }),
  theme: 'light',
  toggleTheme: () => set((s) => ({ theme: s.theme === 'light' ? 'dark' : 'light' })),
}));
```

- [ ] **Step 5: Run test to verify it passes**

Run: `npx vitest run src/lib/apiClient.test.ts`
Expected: PASS.

- [ ] **Step 6: Wire `App.tsx` with routing and providers**

`frontend/src/main.tsx`:

```tsx
import React from 'react';
import ReactDOM from 'react-dom/client';
import { QueryClientProvider } from '@tanstack/react-query';
import { BrowserRouter } from 'react-router-dom';
import App from './App';
import { queryClient } from './lib/queryClient';
import './index.css';

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </QueryClientProvider>
  </React.StrictMode>,
);
```

`frontend/src/App.tsx`:

```tsx
import { Routes, Route } from 'react-router-dom';
import DashboardPage from './pages/DashboardPage';
import QueryPage from './pages/QueryPage';

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<DashboardPage />} />
      <Route path="/query" element={<QueryPage />} />
    </Routes>
  );
}
```

`frontend/src/pages/DashboardPage.tsx` (stub, replaced in Task 14):

```tsx
export default function DashboardPage() {
  return <div className="p-4">Dashboard (Task 14)</div>;
}
```

`frontend/src/pages/QueryPage.tsx` (stub, replaced in Task 15):

```tsx
export default function QueryPage() {
  return <div className="p-4">Query page (Task 15)</div>;
}
```

- [ ] **Step 7: Verify the app renders**

Run: `npm run dev`
Expected: `http://localhost:5173/` shows "Dashboard (Task 14)"; navigating to `/query` shows "Query page (Task 15)".

- [ ] **Step 8: Commit**

```bash
git add frontend/src frontend/vite.config.ts frontend/package.json frontend/package-lock.json
git commit -m "feat: add React app shell, routing, API client, and Zustand store"
```

---

### Task 14: Dashboard page

**Files:**
- Create: `frontend/src/hooks/useProjects.ts`
- Create: `frontend/src/components/ProjectSelector.tsx`
- Modify: `frontend/src/pages/DashboardPage.tsx`
- Test: `frontend/src/components/ProjectSelector.test.tsx`

**Interfaces:**
- Consumes: `apiClient` (Task 13), `GET /api/projects` (Task 3).
- Produces: `ProjectSelector` component and `useProjects` hook — reused by `QueryPage` (Task 15) and `AdminPage` (Task 17).

- [ ] **Step 1: Implement `useProjects` hook**

`frontend/src/hooks/useProjects.ts`:

```ts
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface Project {
  id: string;
  name: string;
}

export function useProjects() {
  return useQuery({
    queryKey: ['projects'],
    queryFn: async () => {
      const { data } = await apiClient.get<Project[]>('/projects');
      return data;
    },
  });
}
```

- [ ] **Step 2: Write the failing test for `ProjectSelector`**

`frontend/src/components/ProjectSelector.test.tsx`:

```tsx
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { ProjectSelector } from './ProjectSelector';
import { apiClient } from '../lib/apiClient';

vi.mock('../lib/apiClient', () => ({ apiClient: { get: vi.fn() } }));

describe('ProjectSelector', () => {
  it('renders project options once loaded', async () => {
    (apiClient.get as any).mockResolvedValue({ data: [{ id: 'proj1', name: 'Project One' }] });
    const client = new QueryClient();

    render(
      <QueryClientProvider client={client}>
        <ProjectSelector value={null} onChange={() => {}} />
      </QueryClientProvider>,
    );

    await waitFor(() => expect(screen.getByText('Project One')).toBeInTheDocument());
  });
});
```

- [ ] **Step 3: Run test to verify it fails**

Run: `npx vitest run src/components/ProjectSelector.test.tsx`
Expected: FAIL — `ProjectSelector` does not exist.

- [ ] **Step 4: Implement `ProjectSelector`**

`frontend/src/components/ProjectSelector.tsx`:

```tsx
import { useProjects } from '../hooks/useProjects';

interface Props {
  value: string | null;
  onChange: (projectId: string) => void;
}

export function ProjectSelector({ value, onChange }: Props) {
  const { data: projects, isLoading } = useProjects();

  if (isLoading) return <div className="animate-pulse h-9 w-48 bg-gray-200 rounded" />;

  return (
    <select
      className="border rounded px-3 py-2"
      value={value ?? ''}
      onChange={(e) => onChange(e.target.value)}
    >
      <option value="" disabled>Select a project</option>
      {projects?.map((p) => (
        <option key={p.id} value={p.id}>{p.name}</option>
      ))}
    </select>
  );
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `npx vitest run src/components/ProjectSelector.test.tsx`
Expected: PASS.

- [ ] **Step 6: Build the Dashboard page**

`frontend/src/pages/DashboardPage.tsx`:

```tsx
import { Link } from 'react-router-dom';
import { ProjectSelector } from '../components/ProjectSelector';
import { useAppStore } from '../store/useAppStore';

export default function DashboardPage() {
  const { selectedProjectId, setSelectedProjectId } = useAppStore();

  return (
    <div className="p-6 max-w-3xl mx-auto space-y-6">
      <h1 className="text-2xl font-semibold">SupportForge AI</h1>
      <ProjectSelector value={selectedProjectId} onChange={setSelectedProjectId} />
      <Link
        to="/query"
        className="inline-block bg-blue-600 text-white px-4 py-2 rounded disabled:opacity-50"
        aria-disabled={!selectedProjectId}
      >
        New Query
      </Link>
    </div>
  );
}
```

- [ ] **Step 7: Verify in the browser**

Run: `npm run dev`, open `http://localhost:5173/`, confirm the project dropdown populates from the live API (Task 3's `/api/projects` with at least one project created via `POST /api/projects`).

- [ ] **Step 8: Commit**

```bash
git add frontend/src
git commit -m "feat: add Dashboard page with project selector"
```

---

### Task 15: Query page (input + screenshot upload)

**Files:**
- Create: `frontend/src/hooks/useChatQuery.ts`
- Create: `frontend/src/components/ScreenshotDropzone.tsx`
- Modify: `frontend/src/pages/QueryPage.tsx`
- Test: `frontend/src/components/ScreenshotDropzone.test.tsx`

**Interfaces:**
- Consumes: `apiClient` (Task 13), `POST /api/chat/query` (Task 5/11), `useAppStore.selectedProjectId` (Task 13).
- Produces: `useChatQuery` mutation hook returning `{ draft, confidence, sources }` — consumed by the Results panel (Task 16).

- [ ] **Step 1: Implement `useChatQuery` mutation hook**

`frontend/src/hooks/useChatQuery.ts`:

```ts
import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface ChatQueryRequest {
  projectId: string;
  query: string;
  screenshotBase64?: string;
}

export interface Source { label: string; url: string; }

export interface ChatQueryResponse {
  draft: string;
  confidence: number;
  sources: Source[];
}

export function useChatQuery() {
  return useMutation({
    mutationFn: async (request: ChatQueryRequest) => {
      const { data } = await apiClient.post<ChatQueryResponse>('/chat/query', request);
      return data;
    },
  });
}
```

- [ ] **Step 2: Write the failing test for `ScreenshotDropzone`**

`frontend/src/components/ScreenshotDropzone.test.tsx`:

```tsx
import { render, fireEvent, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ScreenshotDropzone } from './ScreenshotDropzone';

describe('ScreenshotDropzone', () => {
  it('calls onImageSelected with base64 data when a file is dropped', async () => {
    const onImageSelected = vi.fn();
    render(<ScreenshotDropzone onImageSelected={onImageSelected} />);

    const file = new File(['dummy'], 'error.png', { type: 'image/png' });
    const input = screen.getByTestId('screenshot-input');
    fireEvent.change(input, { target: { files: [file] } });

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(onImageSelected).toHaveBeenCalledWith(expect.any(String));
  });
});
```

- [ ] **Step 3: Run test to verify it fails**

Run: `npx vitest run src/components/ScreenshotDropzone.test.tsx`
Expected: FAIL — `ScreenshotDropzone` does not exist.

- [ ] **Step 4: Implement `ScreenshotDropzone`**

`frontend/src/components/ScreenshotDropzone.tsx`:

```tsx
interface Props {
  onImageSelected: (base64: string) => void;
}

export function ScreenshotDropzone({ onImageSelected }: Props) {
  const readFile = (file: File) => {
    const reader = new FileReader();
    reader.onload = () => {
      const result = reader.result as string;
      onImageSelected(result.split(',')[1] ?? result);
    };
    reader.readAsDataURL(file);
  };

  return (
    <div
      className="border-2 border-dashed rounded p-6 text-center text-sm text-gray-500"
      onDragOver={(e) => e.preventDefault()}
      onDrop={(e) => {
        e.preventDefault();
        const file = e.dataTransfer.files[0];
        if (file) readFile(file);
      }}
      onPaste={(e) => {
        const file = Array.from(e.clipboardData.files)[0];
        if (file) readFile(file);
      }}
    >
      Drag & drop, paste, or
      <label className="text-blue-600 underline cursor-pointer ml-1">
        browse
        <input
          data-testid="screenshot-input"
          type="file"
          accept="image/*"
          className="hidden"
          onChange={(e) => {
            const file = e.target.files?.[0];
            if (file) readFile(file);
          }}
        />
      </label>
    </div>
  );
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `npx vitest run src/components/ScreenshotDropzone.test.tsx`
Expected: PASS.

- [ ] **Step 6: Build the Query page shell (Results panel wired in Task 16)**

`frontend/src/pages/QueryPage.tsx`:

```tsx
import { useState } from 'react';
import { ScreenshotDropzone } from '../components/ScreenshotDropzone';
import { useChatQuery } from '../hooks/useChatQuery';
import { useAppStore } from '../store/useAppStore';
import { ResultsPanel } from '../components/ResultsPanel';

const STEPS = ['Triage', 'Research', 'Analysis', 'Drafting'] as const;

export default function QueryPage() {
  const { selectedProjectId } = useAppStore();
  const [query, setQuery] = useState('');
  const [screenshotBase64, setScreenshotBase64] = useState<string | undefined>();
  const chatQuery = useChatQuery();

  const handleSubmit = () => {
    if (!selectedProjectId) return;
    chatQuery.mutate({ projectId: selectedProjectId, query, screenshotBase64 });
  };

  return (
    <div className="p-6 max-w-3xl mx-auto space-y-4">
      <h1 className="text-xl font-semibold">Ask a question</h1>
      <textarea
        className="w-full border rounded p-3 h-32"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        placeholder="Describe the issue..."
      />
      <ScreenshotDropzone onImageSelected={setScreenshotBase64} />
      <button
        className="bg-blue-600 text-white px-4 py-2 rounded disabled:opacity-50"
        onClick={handleSubmit}
        disabled={!selectedProjectId || !query || chatQuery.isPending}
      >
        Ask Agent
      </button>

      {chatQuery.isPending && (
        <p className="text-sm text-gray-500">{STEPS.join(' → ')}...</p>
      )}

      {chatQuery.data && <ResultsPanel result={chatQuery.data} />}
      {chatQuery.isError && (
        <p className="text-sm text-red-600">
          Something went wrong. <button className="underline" onClick={handleSubmit}>Retry</button>
        </p>
      )}
    </div>
  );
}
```

- [ ] **Step 7: Commit**

```bash
git add frontend/src
git commit -m "feat: add Query page with screenshot dropzone and chat query hook"
```

---

### Task 16: Results panel (citations, confidence, actions)

**Files:**
- Create: `frontend/src/components/ResultsPanel.tsx`
- Create: `frontend/src/components/ConfidenceBadge.tsx`
- Install: `react-markdown`
- Test: `frontend/src/components/ResultsPanel.test.tsx`

**Interfaces:**
- Consumes: `ChatQueryResponse` (Task 15).
- Produces: `ResultsPanel` — action button handlers (`onCopy`, `onMarkUseful`, `onEscalate`) call the feedback API added in Task 18; stub them as local no-ops here and wire in Task 18.

- [ ] **Step 1: Install markdown renderer**

```bash
cd frontend
npm install react-markdown
```

- [ ] **Step 2: Write the failing test for `ResultsPanel`**

`frontend/src/components/ResultsPanel.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ResultsPanel } from './ResultsPanel';

describe('ResultsPanel', () => {
  it('renders the draft, sources, and confidence badge', () => {
    render(
      <ResultsPanel
        result={{
          draft: '**Try restarting the service.**',
          confidence: 0.85,
          sources: [{ label: 'KB: restart.md', url: 'kb/restart.md' }],
        }}
      />,
    );

    expect(screen.getByText('Try restarting the service.')).toBeInTheDocument();
    expect(screen.getByText('KB: restart.md')).toBeInTheDocument();
    expect(screen.getByText('High confidence')).toBeInTheDocument();
  });
});
```

- [ ] **Step 3: Run test to verify it fails**

Run: `npx vitest run src/components/ResultsPanel.test.tsx`
Expected: FAIL — `ResultsPanel` does not exist.

- [ ] **Step 4: Implement `ConfidenceBadge` and `ResultsPanel`**

`frontend/src/components/ConfidenceBadge.tsx`:

```tsx
export function ConfidenceBadge({ confidence }: { confidence: number }) {
  const label = confidence >= 0.7 ? 'High confidence' : confidence >= 0.4 ? 'Medium confidence' : 'Low confidence';
  const color = confidence >= 0.7 ? 'bg-green-100 text-green-800' : confidence >= 0.4 ? 'bg-yellow-100 text-yellow-800' : 'bg-red-100 text-red-800';

  return <span className={`text-xs px-2 py-1 rounded ${color}`}>{label}</span>;
}
```

`frontend/src/components/ResultsPanel.tsx`:

```tsx
import ReactMarkdown from 'react-markdown';
import { ChatQueryResponse } from '../hooks/useChatQuery';
import { ConfidenceBadge } from './ConfidenceBadge';

interface Props {
  result: ChatQueryResponse;
  onCopy?: () => void;
  onMarkUseful?: (useful: boolean) => void;
  onEscalate?: () => void;
}

export function ResultsPanel({ result, onCopy, onMarkUseful, onEscalate }: Props) {
  return (
    <div className="border rounded p-4 space-y-3">
      <div className="flex justify-between items-center">
        <ConfidenceBadge confidence={result.confidence} />
      </div>

      <div className="prose prose-sm max-w-none">
        <ReactMarkdown>{result.draft}</ReactMarkdown>
      </div>

      {result.sources.length > 0 && (
        <div className="text-sm text-gray-600">
          <strong>Sources:</strong>
          <ul className="list-disc list-inside">
            {result.sources.map((s) => (
              <li key={s.url}>{s.label}</li>
            ))}
          </ul>
        </div>
      )}

      <div className="flex gap-2 pt-2">
        <button className="text-sm border rounded px-3 py-1" onClick={onCopy}>Copy Response</button>
        <button className="text-sm border rounded px-3 py-1" onClick={() => onMarkUseful?.(true)}>Mark Useful</button>
        <button className="text-sm border rounded px-3 py-1" onClick={() => onMarkUseful?.(false)}>Not Useful</button>
        <button className="text-sm border rounded px-3 py-1" onClick={onEscalate}>Escalate</button>
      </div>
    </div>
  );
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `npx vitest run src/components/ResultsPanel.test.tsx`
Expected: PASS.

- [ ] **Step 6: Verify visually end-to-end**

Run backend (`dotnet run --project backend/SupportForge.Api`) and frontend (`npm run dev`), navigate to `/query`, submit a real question against an ingested project, confirm the Markdown answer, sources list, and confidence badge render correctly.

- [ ] **Step 7: Commit**

```bash
git add frontend/src frontend/package.json frontend/package-lock.json
git commit -m "feat: add Results panel with markdown rendering, sources, and confidence badge"
```

---

## Day 6 — Polish & Admin

### Task 17: Admin config page

**Files:**
- Create: `frontend/src/pages/AdminPage.tsx`
- Create: `frontend/src/hooks/useCreateProject.ts`
- Create: `frontend/src/hooks/useTriggerIngestion.ts`
- Modify: `frontend/src/App.tsx` (add `/admin` route)
- Test: `frontend/src/pages/AdminPage.test.tsx`

**Interfaces:**
- Consumes: `POST /api/projects` (Task 3), `POST /api/ingestion/trigger` (Task 6), `GET /api/projects/{id}/freshness` (Task 12).
- Produces: nothing further consumed downstream — this is the last screen the PRD requires for MVP.

- [ ] **Step 1: Implement `useCreateProject` and `useTriggerIngestion` hooks**

`frontend/src/hooks/useCreateProject.ts`:

```ts
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface CreateProjectRequest {
  id: string;
  name: string;
  repos: { owner: string; repo: string; defaultBranch: string }[];
  kbSources: { type: 'Documents'; location: string }[];
}

export function useCreateProject() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: async (request: CreateProjectRequest) => {
      const { data } = await apiClient.post('/projects', request);
      return data;
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['projects'] }),
  });
}
```

`frontend/src/hooks/useTriggerIngestion.ts`:

```ts
import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export function useTriggerIngestion() {
  return useMutation({
    mutationFn: async (projectId: string) => {
      await apiClient.post('/ingestion/trigger', { projectId });
    },
  });
}
```

- [ ] **Step 2: Write the failing test for `AdminPage`**

`frontend/src/pages/AdminPage.test.tsx`:

```tsx
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import AdminPage from './AdminPage';
import { apiClient } from '../lib/apiClient';

vi.mock('../lib/apiClient', () => ({ apiClient: { get: vi.fn().mockResolvedValue({ data: [] }), post: vi.fn().mockResolvedValue({ data: {} }) } }));

describe('AdminPage', () => {
  it('submits a new project with the entered id and name', async () => {
    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <AdminPage />
      </QueryClientProvider>,
    );

    fireEvent.change(screen.getByLabelText('Project ID'), { target: { value: 'proj2' } });
    fireEvent.change(screen.getByLabelText('Project Name'), { target: { value: 'Project Two' } });
    fireEvent.click(screen.getByText('Create Project'));

    await waitFor(() => expect(apiClient.post).toHaveBeenCalledWith('/projects', expect.objectContaining({ id: 'proj2', name: 'Project Two' })));
  });
});
```

- [ ] **Step 3: Run test to verify it fails**

Run: `npx vitest run src/pages/AdminPage.test.tsx`
Expected: FAIL — `AdminPage` does not exist.

- [ ] **Step 4: Implement `AdminPage`**

`frontend/src/pages/AdminPage.tsx`:

```tsx
import { useState } from 'react';
import { useProjects } from '../hooks/useProjects';
import { useCreateProject } from '../hooks/useCreateProject';
import { useTriggerIngestion } from '../hooks/useTriggerIngestion';

export default function AdminPage() {
  const { data: projects } = useProjects();
  const createProject = useCreateProject();
  const triggerIngestion = useTriggerIngestion();

  const [id, setId] = useState('');
  const [name, setName] = useState('');
  const [repoOwner, setRepoOwner] = useState('');
  const [repoName, setRepoName] = useState('');
  const [kbFolder, setKbFolder] = useState('');

  const handleCreate = () => {
    createProject.mutate({
      id,
      name,
      repos: repoOwner && repoName ? [{ owner: repoOwner, repo: repoName, defaultBranch: 'main' }] : [],
      kbSources: kbFolder ? [{ type: 'Documents', location: kbFolder }] : [],
    });
  };

  return (
    <div className="p-6 max-w-2xl mx-auto space-y-6">
      <h1 className="text-xl font-semibold">Project Administration</h1>

      <section className="space-y-2 border rounded p-4">
        <h2 className="font-medium">Add Project</h2>
        <label className="block text-sm">Project ID
          <input className="border rounded w-full p-2" value={id} onChange={(e) => setId(e.target.value)} />
        </label>
        <label className="block text-sm">Project Name
          <input className="border rounded w-full p-2" value={name} onChange={(e) => setName(e.target.value)} />
        </label>
        <label className="block text-sm">GitHub Repo Owner
          <input className="border rounded w-full p-2" value={repoOwner} onChange={(e) => setRepoOwner(e.target.value)} />
        </label>
        <label className="block text-sm">GitHub Repo Name
          <input className="border rounded w-full p-2" value={repoName} onChange={(e) => setRepoName(e.target.value)} />
        </label>
        <label className="block text-sm">KB Documents Folder
          <input className="border rounded w-full p-2" value={kbFolder} onChange={(e) => setKbFolder(e.target.value)} />
        </label>
        <button className="bg-blue-600 text-white px-4 py-2 rounded" onClick={handleCreate}>Create Project</button>
      </section>

      <section className="space-y-2 border rounded p-4">
        <h2 className="font-medium">Existing Projects</h2>
        <ul className="space-y-2">
          {projects?.map((p) => (
            <li key={p.id} className="flex justify-between items-center">
              <span>{p.name} ({p.id})</span>
              <button
                className="text-sm border rounded px-3 py-1"
                onClick={() => triggerIngestion.mutate(p.id)}
                disabled={triggerIngestion.isPending}
              >
                Re-index
              </button>
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `npx vitest run src/pages/AdminPage.test.tsx`
Expected: PASS.

- [ ] **Step 6: Wire the route**

`frontend/src/App.tsx` — add `<Route path="/admin" element={<AdminPage />} />` and a nav link from `DashboardPage`.

- [ ] **Step 7: Commit**

```bash
git add frontend/src
git commit -m "feat: add admin page for project creation and manual re-indexing"
```

---

### Task 18: Feedback system (Mark Useful / Escalate)

**Files:**
- Create: `backend/SupportForge.Core/Entities/FeedbackEntry.cs`
- Create: `backend/SupportForge.Core/IFeedbackRepository.cs`
- Create: `backend/SupportForge.Core/JsonFileFeedbackRepository.cs`
- Create: `backend/SupportForge.Api/Controllers/FeedbackController.cs`
- Modify: `frontend/src/pages/QueryPage.tsx` (wire `ResultsPanel` callbacks)
- Create: `frontend/src/hooks/useSubmitFeedback.ts`
- Test: `backend/SupportForge.Api.Tests/Controllers/FeedbackControllerTests.cs`

**Interfaces:**
- Consumes: same JSON-file persistence pattern as `IProjectRepository` (Task 3).
- Produces: `POST /api/feedback` — this is the one MVP-required extension beyond TSD §6's four listed routes, needed for the PRD's "Mark as Useful/Not Useful", "Escalate to Engineer" buttons (PRD §5b).

- [ ] **Step 1: Define `FeedbackEntry` and repository**

`backend/SupportForge.Core/Entities/FeedbackEntry.cs`:

```csharp
namespace SupportForge.Core.Entities;

public sealed record FeedbackEntry(string ProjectId, string Query, bool? Useful, bool Escalated, DateTimeOffset CreatedAt);
```

`backend/SupportForge.Core/IFeedbackRepository.cs`:

```csharp
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface IFeedbackRepository
{
    Task AddAsync(FeedbackEntry entry, CancellationToken ct = default);
}
```

`backend/SupportForge.Core/JsonFileFeedbackRepository.cs` (append-only, mirrors `JsonFileProjectRepository`'s locking pattern):

```csharp
using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed class JsonFileFeedbackRepository : IFeedbackRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileFeedbackRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "feedback.json");
    }

    public async Task AddAsync(FeedbackEntry entry, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<List<FeedbackEntry>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
                : new List<FeedbackEntry>();

            all.Add(entry);
            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }), ct);
        }
        finally { _lock.Release(); }
    }
}
```

- [ ] **Step 2: Write the failing test for `FeedbackController`**

`backend/SupportForge.Api.Tests/Controllers/FeedbackControllerTests.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using SupportForge.Api.Controllers;
using SupportForge.Core;
using Xunit;

namespace SupportForge.Api.Tests.Controllers;

public class FeedbackControllerTests
{
    [Fact]
    public async Task Submit_ReturnsOk_AndPersistsEntry()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileFeedbackRepository(tempDir);
        var controller = new FeedbackController(repo);

        var result = await controller.Submit(new FeedbackController.SubmitRequest("proj1", "how do I reset", true, false));

        Assert.IsType<OkResult>(result);
        Assert.True(File.Exists(Path.Combine(tempDir, "feedback.json")));

        Directory.Delete(tempDir, recursive: true);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test backend/SupportForge.Api.Tests --filter FeedbackControllerTests`
Expected: FAIL — `FeedbackController` does not exist.

- [ ] **Step 4: Implement `FeedbackController`**

`backend/SupportForge.Api/Controllers/FeedbackController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using SupportForge.Core;
using SupportForge.Core.Entities;

namespace SupportForge.Api.Controllers;

[ApiController]
[Route("api/feedback")]
public class FeedbackController : ControllerBase
{
    private readonly IFeedbackRepository _repo;

    public FeedbackController(IFeedbackRepository repo) => _repo = repo;

    public sealed record SubmitRequest(string ProjectId, string Query, bool? Useful, bool Escalated);

    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] SubmitRequest request, CancellationToken ct = default)
    {
        await _repo.AddAsync(new FeedbackEntry(request.ProjectId, request.Query, request.Useful, request.Escalated, DateTimeOffset.UtcNow), ct);
        return Ok();
    }
}
```

Register in `Program.cs`: `builder.Services.AddSingleton<IFeedbackRepository>(new JsonFileFeedbackRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));`

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test backend/SupportForge.Api.Tests --filter FeedbackControllerTests`
Expected: PASS.

- [ ] **Step 6: Wire the frontend**

`frontend/src/hooks/useSubmitFeedback.ts`:

```ts
import { useMutation } from '@tanstack/react-query';
import { apiClient } from '../lib/apiClient';

export interface FeedbackRequest {
  projectId: string;
  query: string;
  useful?: boolean;
  escalated: boolean;
}

export function useSubmitFeedback() {
  return useMutation({
    mutationFn: async (request: FeedbackRequest) => {
      await apiClient.post('/feedback', request);
    },
  });
}
```

`frontend/src/pages/QueryPage.tsx` — pass real handlers to `<ResultsPanel>`:

```tsx
const submitFeedback = useSubmitFeedback();

<ResultsPanel
  result={chatQuery.data}
  onCopy={() => navigator.clipboard.writeText(chatQuery.data.draft)}
  onMarkUseful={(useful) => submitFeedback.mutate({ projectId: selectedProjectId!, query, useful, escalated: false })}
  onEscalate={() => submitFeedback.mutate({ projectId: selectedProjectId!, query, escalated: true })}
/>
```

- [ ] **Step 7: Commit**

```bash
git add backend/SupportForge.Core backend/SupportForge.Api frontend/src
git commit -m "feat: add feedback endpoint and wire Mark Useful/Escalate actions"
```

---

### Task 19: Error boundaries and loading skeletons

**Files:**
- Create: `frontend/src/components/ErrorBoundary.tsx`
- Modify: `frontend/src/App.tsx` (wrap routes)
- Modify: `frontend/src/components/ProjectSelector.tsx` (skeleton already added in Task 14 — verify)
- Test: `frontend/src/components/ErrorBoundary.test.tsx`

**Interfaces:**
- Consumes: nothing new.
- Produces: `ErrorBoundary` wraps `<App />`'s route tree — final structural piece required by TSD §4 ("Error boundaries with retry").

- [ ] **Step 1: Write the failing test for `ErrorBoundary`**

`frontend/src/components/ErrorBoundary.test.tsx`:

```tsx
import { render, screen, fireEvent } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ErrorBoundary } from './ErrorBoundary';

function Boom(): JSX.Element {
  throw new Error('boom');
}

describe('ErrorBoundary', () => {
  it('renders a retry UI when a child throws', () => {
    render(
      <ErrorBoundary>
        <Boom />
      </ErrorBoundary>,
    );

    expect(screen.getByText(/something went wrong/i)).toBeInTheDocument();
    expect(screen.getByText('Retry')).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `npx vitest run src/components/ErrorBoundary.test.tsx`
Expected: FAIL — `ErrorBoundary` does not exist.

- [ ] **Step 3: Implement `ErrorBoundary`**

`frontend/src/components/ErrorBoundary.tsx`:

```tsx
import { Component, ReactNode } from 'react';

interface Props { children: ReactNode; }
interface State { hasError: boolean; }

export class ErrorBoundary extends Component<Props, State> {
  state: State = { hasError: false };

  static getDerivedStateFromError(): State {
    return { hasError: true };
  }

  render() {
    if (this.state.hasError) {
      return (
        <div className="p-6 text-center space-y-2">
          <p className="text-red-600">Something went wrong.</p>
          <button className="underline" onClick={() => this.setState({ hasError: false })}>Retry</button>
        </div>
      );
    }

    return this.props.children;
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `npx vitest run src/components/ErrorBoundary.test.tsx`
Expected: PASS.

- [ ] **Step 5: Wrap the route tree**

`frontend/src/App.tsx`:

```tsx
import { Routes, Route } from 'react-router-dom';
import { ErrorBoundary } from './components/ErrorBoundary';
import DashboardPage from './pages/DashboardPage';
import QueryPage from './pages/QueryPage';
import AdminPage from './pages/AdminPage';

export default function App() {
  return (
    <ErrorBoundary>
      <Routes>
        <Route path="/" element={<DashboardPage />} />
        <Route path="/query" element={<QueryPage />} />
        <Route path="/admin" element={<AdminPage />} />
      </Routes>
    </ErrorBoundary>
  );
}
```

- [ ] **Step 6: Commit**

```bash
git add frontend/src
git commit -m "feat: add error boundary with retry around the route tree"
```

---

### Task 20: Token usage tracking

**Files:**
- Create: `backend/SupportForge.Core/Entities/TokenUsageEntry.cs`
- Create: `backend/SupportForge.Core/ITokenUsageRepository.cs`
- Create: `backend/SupportForge.Core/JsonFileTokenUsageRepository.cs`
- Modify: `backend/SupportForge.Agents/OpenAiLlmClient.cs` (surface token counts from OpenAI response)
- Modify: `backend/SupportForge.Api/Controllers/ChatController.cs` (record usage after each query)
- Test: `backend/SupportForge.Api.Tests/TokenUsageTests.cs`

**Interfaces:**
- Consumes: OpenAI response `usage.total_tokens` field.
- Produces: `ITokenUsageRepository.AddAsync` — recorded per query, exposed via `GET /api/projects/{id}/usage` for the Analytics screen the PRD lists as MVP-adjacent (PRD §5d); TSD's own risk mitigation says prioritize the core loop, so this is intentionally the simplest possible counter, not a full analytics dashboard.

- [ ] **Step 1: Define `TokenUsageEntry` and repository (same JSON pattern as Tasks 3/18)**

`backend/SupportForge.Core/Entities/TokenUsageEntry.cs`:

```csharp
namespace SupportForge.Core.Entities;

public sealed record TokenUsageEntry(string ProjectId, int TotalTokens, DateTimeOffset CreatedAt);
```

`backend/SupportForge.Core/ITokenUsageRepository.cs`:

```csharp
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public interface ITokenUsageRepository
{
    Task AddAsync(TokenUsageEntry entry, CancellationToken ct = default);
    Task<int> GetTotalForProjectAsync(string projectId, CancellationToken ct = default);
}
```

`backend/SupportForge.Core/JsonFileTokenUsageRepository.cs`:

```csharp
using System.Text.Json;
using SupportForge.Core.Entities;

namespace SupportForge.Core;

public sealed class JsonFileTokenUsageRepository : ITokenUsageRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonFileTokenUsageRepository(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "token_usage.json");
    }

    public async Task AddAsync(TokenUsageEntry entry, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = File.Exists(_filePath)
                ? JsonSerializer.Deserialize<List<TokenUsageEntry>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new()
                : new List<TokenUsageEntry>();

            all.Add(entry);
            await File.WriteAllTextAsync(_filePath, JsonSerializer.Serialize(all), ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<int> GetTotalForProjectAsync(string projectId, CancellationToken ct = default)
    {
        if (!File.Exists(_filePath)) return 0;

        await _lock.WaitAsync(ct);
        try
        {
            var all = JsonSerializer.Deserialize<List<TokenUsageEntry>>(await File.ReadAllTextAsync(_filePath, ct)) ?? new();
            return all.Where(e => e.ProjectId == projectId).Sum(e => e.TotalTokens);
        }
        finally { _lock.Release(); }
    }
}
```

- [ ] **Step 2: Write the failing test**

`backend/SupportForge.Api.Tests/TokenUsageTests.cs`:

```csharp
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests;

public class TokenUsageTests
{
    [Fact]
    public async Task AddAsync_Then_GetTotalForProjectAsync_SumsTokensForThatProjectOnly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var repo = new JsonFileTokenUsageRepository(tempDir);

        await repo.AddAsync(new TokenUsageEntry("proj1", 100, DateTimeOffset.UtcNow));
        await repo.AddAsync(new TokenUsageEntry("proj1", 50, DateTimeOffset.UtcNow));
        await repo.AddAsync(new TokenUsageEntry("proj2", 999, DateTimeOffset.UtcNow));

        var total = await repo.GetTotalForProjectAsync("proj1");

        Assert.Equal(150, total);
        Directory.Delete(tempDir, recursive: true);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test backend/SupportForge.Api.Tests --filter TokenUsageTests`
Expected: FAIL — types don't exist.

- [ ] **Step 4: Run test to verify it passes after implementation above**

Run: `dotnet test backend/SupportForge.Api.Tests --filter TokenUsageTests`
Expected: PASS.

- [ ] **Step 5: Surface token counts from `OpenAiLlmClient` and record them in `ChatController`**

`backend/SupportForge.Agents/ILlmClient.cs` — add `Task<(string Content, int TotalTokens)> CompleteWithUsageAsync(...)` is unnecessary churn; simpler: add a settable `LastTotalTokens` property to `OpenAiLlmClient`, populated from the `usage` field on every `CompleteAsync`/`AnalyzeImageAsync` call, and read it in `ChatController` after `_pipeline.RunAsync` completes.

`backend/SupportForge.Agents/OpenAiLlmClient.cs` — add `public int LastTotalTokens { get; private set; }`, and after each response deserialization: `LastTotalTokens = body?.Usage?.TotalTokens ?? 0;` (add `Usage` DTO with `TotalTokens` field, and `record ChatResponse` gains a `Usage` property).

`backend/SupportForge.Api/Controllers/ChatController.cs` — inject `ITokenUsageRepository` and `ILlmClient`, and after building the response: `await _tokenUsage.AddAsync(new TokenUsageEntry(request.ProjectId, ((OpenAiLlmClient)_llm).LastTotalTokens, DateTimeOffset.UtcNow), ct);` — cast is acceptable here since `LastTotalTokens` is an implementation-specific instrumentation detail, not part of the `ILlmClient` contract.

Register in `Program.cs`: `builder.Services.AddSingleton<ITokenUsageRepository>(new JsonFileTokenUsageRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));`

- [ ] **Step 6: Commit**

```bash
git add backend/SupportForge.Core backend/SupportForge.Agents backend/SupportForge.Api
git commit -m "feat: add per-project token usage tracking"
```

---

## Day 7 — Testing, Deployment & Handover

### Task 21: End-to-end integration test with real project data

**Files:**
- Create: `backend/SupportForge.Api.Tests/Integration/EndToEndQueryTests.cs`
- Create: `backend/SupportForge.Api.Tests/Integration/TestData/sample-kb/getting-started.md`

**Interfaces:**
- Consumes: the full running pipeline via `WebApplicationFactory<Program>` (in-process, real Chroma required locally — see Step 1).

- [ ] **Step 1: Start a local Chroma instance for integration testing**

```bash
docker run -d --name supportforge-chroma -p 8000:8000 chromadb/chroma
```

Expected: `curl http://localhost:8000/api/v1/heartbeat` returns a JSON heartbeat payload.

- [ ] **Step 2: Add a sample KB doc for the test project**

`backend/SupportForge.Api.Tests/Integration/TestData/sample-kb/getting-started.md`:

```markdown
# Getting Started

To reset your password, go to Settings > Security > Reset Password and follow the emailed link.
```

- [ ] **Step 3: Write the integration test**

`backend/SupportForge.Api.Tests/Integration/EndToEndQueryTests.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using SupportForge.Api.Contracts;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Integration;

public class EndToEndQueryTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EndToEndQueryTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Query_AfterIngestion_ReturnsAnswerCitingKbSource()
    {
        var client = _factory.CreateClient();
        var kbPath = Path.Combine(AppContext.BaseDirectory, "Integration", "TestData", "sample-kb");

        var project = new Project
        {
            Id = "e2e-proj",
            Name = "E2E Project",
            KbSources = new List<KbSourceConfig> { new(KbSourceType.Documents, kbPath, null) },
        };
        await client.PostAsJsonAsync("/api/projects", project);
        await client.PostAsJsonAsync("/api/ingestion/trigger", new { ProjectId = "e2e-proj" });

        await Task.Delay(TimeSpan.FromSeconds(10)); // background ingestion queue drain

        var response = await client.PostAsJsonAsync("/api/chat/query", new ChatQueryRequest
        {
            ProjectId = "e2e-proj",
            Query = "How do I reset my password?",
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ChatQueryResponse>();

        Assert.NotNull(body);
        Assert.Contains("password", body!.Draft, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(body.Sources, s => s.Url.Contains("getting-started.md"));
    }
}
```

Requires `OpenAI:ApiKey` set in the test environment (`appsettings.Development.json` or `OPENAI__APIKEY` env var) — this test makes real LLM calls and is intentionally excluded from CI by tagging it `[Trait("Category", "Integration")]` and running it manually before deployment, per TSD §5 Day 7 ("End-to-end testing with real project data").

- [ ] **Step 4: Run the integration test manually**

Run: `dotnet test backend/SupportForge.Api.Tests --filter EndToEndQueryTests`
Expected: PASS — confirms the full Coordinator pipeline (Triage → KbResearcher → CodeAnalyzer → VisionAnalyzer → Drafter) produces a cited answer end-to-end.

- [ ] **Step 5: Commit**

```bash
git add backend/SupportForge.Api.Tests
git commit -m "test: add end-to-end integration test covering ingestion through chat query"
```

---

### Task 22: Backend deployment to EC2 + IIS

**Files:**
- Create: `backend/SupportForge.Api/web.config`
- Create: `docs/deployment/backend-runbook.md`

**Interfaces:**
- Consumes: a published `dotnet publish` output.
- Produces: a running IIS site at the configured binding, proxying to the ASP.NET Core Module.

- [ ] **Step 1: Publish the API**

```bash
dotnet publish backend/SupportForge.Api -c Release -o publish/api
```

Expected: `publish/api/` contains `SupportForge.Api.dll`, `web.config`, and dependencies.

- [ ] **Step 2: Confirm/adjust `web.config` for IIS + ASP.NET Core Module**

`backend/SupportForge.Api/web.config` (generated by `dotnet publish`; verify these values, since IIS App Pool must run "No Managed Code"):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <handlers>
      <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
    </handlers>
    <aspNetCore processPath="dotnet" arguments=".\SupportForge.Api.dll" stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout" hostingModel="inprocess" />
  </system.webServer>
</configuration>
```

- [ ] **Step 3: Write the deployment runbook**

`docs/deployment/backend-runbook.md`:

```markdown
# Backend Deployment Runbook (EC2 + IIS)

## Prerequisites on the EC2 Windows instance
1. Install IIS with the "Web Server (IIS)" role and ASP.NET Core Hosting Bundle for .NET 8.
2. Install Docker Desktop (or use Pinecone in prod — see `VectorStore:Provider` config) for the local Chroma container if not using Pinecone.
3. Create an Application Pool named `SupportForgeApi` with .NET CLR version "No Managed Code".

## Deploy steps
1. From the dev machine: `dotnet publish backend/SupportForge.Api -c Release -o publish/api`
2. Copy `publish/api/*` to `C:\inetpub\supportforge-api\` on the EC2 instance.
3. In IIS Manager, create a site "SupportForge API" bound to port 5000, physical path `C:\inetpub\supportforge-api\`, using the `SupportForgeApi` app pool.
4. Set environment variables on the App Pool (or `appsettings.Production.json`): `OpenAI__ApiKey`, `VectorStore__Provider=Pinecone`, `VectorStore__Pinecone__ApiKey`, `VectorStore__Pinecone__Environment`.
5. Start the site; verify `http://<ec2-host>:5000/health` returns `{"status":"ok"}`.

## Rollback
Keep the previous `publish/api` folder as `publish/api-previous/`; to roll back, stop the IIS site, swap folder contents, restart the site.
```

- [ ] **Step 4: Verify locally with IIS Express or a local IIS install (smoke test)**

Run: `dotnet publish backend/SupportForge.Api -c Release -o publish/api && cd publish/api && dotnet SupportForge.Api.dll`
Expected: app starts on the configured port; `curl http://localhost:5000/health` returns `{"status":"ok"}`.

- [ ] **Step 5: Commit**

```bash
git add backend/SupportForge.Api/web.config docs/deployment/backend-runbook.md
git commit -m "docs: add IIS deployment runbook and verify published output"
```

---

### Task 23: Frontend build & static hosting under IIS

**Files:**
- Modify: `docs/deployment/backend-runbook.md` → split frontend section into new file
- Create: `docs/deployment/frontend-runbook.md`
- Create: `frontend/.env.production`

**Interfaces:**
- Consumes: `frontend`'s Vite build output (`dist/`).
- Produces: a static site served either from the same IIS instance under a sub-path, or a separate IIS site pointing `apiClient`'s `baseURL` at the deployed backend.

- [ ] **Step 1: Configure the production API base URL**

`frontend/.env.production`:

```
VITE_API_BASE_URL=http://<ec2-host>:5000/api
```

- [ ] **Step 2: Build the frontend**

```bash
cd frontend
npm run build
```

Expected: `frontend/dist/` contains `index.html`, `assets/*.js`, `assets/*.css`.

- [ ] **Step 3: Write the frontend deployment runbook**

`docs/deployment/frontend-runbook.md`:

```markdown
# Frontend Deployment Runbook (IIS static hosting)

## Deploy steps
1. `cd frontend && npm run build` — produces `dist/`.
2. Copy `dist/*` to `C:\inetpub\supportforge-ui\` on the EC2 instance.
3. In IIS Manager, create a site "SupportForge UI" bound to port 80, physical path `C:\inetpub\supportforge-ui\`.
4. Add a `web.config` for SPA routing (React Router client-side routes must fall back to `index.html`):

\`\`\`xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="React Routes" stopProcessing="true">
          <match url=".*" />
          <conditions logicalGrouping="MatchAll">
            <add input="{REQUEST_FILENAME}" matchType="IsFile" negate="true" />
            <add input="{REQUEST_FILENAME}" matchType="IsDirectory" negate="true" />
          </conditions>
          <action type="Rewrite" url="/index.html" />
        </rule>
      </rules>
    </rewrite>
  </system.webServer>
</configuration>
\`\`\`

(Requires the IIS URL Rewrite module.)

5. Verify `http://<ec2-host>/` loads the Dashboard and `http://<ec2-host>/query` works on direct navigation (not just client-side nav) — this proves the rewrite rule is correct.
```

- [ ] **Step 4: Verify the production build locally**

Run: `npx serve dist` (from `frontend/`, after `npm install -g serve` or `npx serve`)
Expected: app loads at the served port, all three routes (`/`, `/query`, `/admin`) render without console errors, and API calls hit the URL configured in `.env.production` (confirm via browser network tab or a temporary local override).

- [ ] **Step 5: Commit**

```bash
git add frontend/.env.production docs/deployment/frontend-runbook.md
git commit -m "docs: add frontend production build and IIS static hosting runbook"
```

---

### Task 24: Configuration guide & handover doc

**Files:**
- Create: `docs/deployment/configuration-guide.md`

**Interfaces:**
- Consumes: every config key introduced across this plan (`OpenAI:ApiKey`, `VectorStore:*`, project JSON schema).
- Produces: the single reference document handed to the next engineer/support-lead onboarding a new project — the TSD's Day 7 "Configuration guide + runbook" and "Demo + knowledge transfer" deliverables.

- [ ] **Step 1: Write the configuration guide**

`docs/deployment/configuration-guide.md`:

```markdown
# SupportForge AI — Configuration Guide

## Required configuration (`appsettings.Production.json` or environment variables)

| Key | Purpose | Example |
|---|---|---|
| `OpenAI:ApiKey` | LLM completions, embeddings, vision | `sk-...` |
| `VectorStore:Provider` | `Chroma` (local) or `Pinecone` (prod) | `Pinecone` |
| `VectorStore:Chroma:BaseUrl` | Chroma server URL (local only) | `http://localhost:8000` |
| `VectorStore:Pinecone:ApiKey` | Pinecone API key (prod only) | `...` |
| `VectorStore:Pinecone:Environment` | Pinecone environment/region | `us-east-1` |

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

## Demo script for handover
1. Create a project via `/admin` pointing at a small real KB folder + one small GitHub repo.
2. Trigger ingestion, wait ~30s–2min depending on repo size.
3. From `/query`, ask a KB-answerable question — confirm citations point at the right file.
4. Ask a code-related question — confirm the Code Analyzer agent fires (check `intent` was `code_issue`) and code file citations appear.
5. Upload a screenshot with an error dialog and ask "why am I seeing this?" — confirm Vision findings feed into the draft.
6. Click Mark Useful / Escalate — confirm `App_Data/feedback.json` records the entry.
```

- [ ] **Step 2: Commit**

```bash
git add docs/deployment/configuration-guide.md
git commit -m "docs: add configuration guide, onboarding steps, and handover demo script"
```

---

## Post-Plan Follow-Ups (explicitly not in the 7-day MVP)

- Entra ID authentication + role-based access (PRD §7 non-functional requirement) — flagged as a known gap in Task 24; needs its own plan before any deployment reachable outside the internal network.
- Swap the `IAgent`/`AgentPipeline` abstraction in `SupportForge.Agents` for the real Microsoft Agent Framework SDK once its concrete 2026 API surface is confirmed (see Global Constraints assumption flag).
- Pinecone `IVectorStoreService` implementation — `VectorStoreServiceCollectionExtensions` currently throws `NotSupportedException` for `Provider=Pinecone`; needed before production cutover.
- Confluence KB connector and Slack/Teams integration — PRD §9 Phase 2 roadmap items.
