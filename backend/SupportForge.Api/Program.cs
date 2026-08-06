using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.Api.HealthChecks;
using SupportForge.Api.Identity;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Documents;
using SupportForge.Ingestion.Graph;
using SupportForge.VectorStore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    const string bearerScheme = "Bearer";
    options.AddSecurityDefinition(bearerScheme, new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Description = "Paste the access token from POST /api/auth/token (no \"Bearer \" prefix needed).",
    });
    options.AddSecurityRequirement(document => new Microsoft.OpenApi.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.OpenApiSecuritySchemeReference(bearerScheme, document)] = [],
    });
});
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    is { Length: > 0 } configuredOrigins
        ? configuredOrigins
        : new[] { "http://localhost:5173" };
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod());
});
builder.Services.AddVectorStore(builder.Configuration);
builder.Services.AddSingleton<IProjectRepository>(
    new JsonFileProjectRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
builder.Services.AddSingleton<IProjectMembershipRepository>(
    new JsonFileProjectMembershipRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
builder.Services.AddSingleton<IFeedbackRepository>(
    new JsonFileFeedbackRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
builder.Services.AddSingleton<IDeadLetterRepository>(
    new JsonFileDeadLetterRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
builder.Services.AddSingleton<IContentHashRepository>(
    new JsonFileContentHashRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
builder.Services.AddSingleton<ITokenUsageRepository>(
    new JsonFileTokenUsageRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
builder.Services.AddSingleton<IConversationRepository>(
    new JsonFileConversationRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
builder.Services.AddSingleton<IChatMessageRepository>(
    new JsonFileChatMessageRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
builder.Services.AddSingleton<IUserRepository>(
    new JsonFileUserRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));

// KTD1: Identity's storage abstractions against a JSON-file-backed store (CustomUserStore),
// not EF Core -- this repo has no database anywhere else. PasswordHasher<AppUser> (registered
// by AddIdentityCore) handles hashing; no custom hashing code needed.
builder.Services.AddIdentityCore<AppUser>()
    .AddUserStore<CustomUserStore>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = JwtTokenFactory.ResolveIssuer(builder.Configuration),
            ValidateAudience = true,
            ValidAudience = JwtTokenFactory.ResolveAudience(builder.Configuration),
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = JwtTokenFactory.ResolveSigningKey(builder.Configuration),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

// KTD4: built-in RateLimiter middleware (no new package), partitioned by authenticated user ID
// once a request carries a valid JWT, falling back to client IP for anonymous requests
// (e.g. /health, /api/auth/token, or any request that hasn't authenticated yet).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var partitionKey = httpContext.User.Identity?.IsAuthenticated == true
            ? httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "authenticated"
            : httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Read config lazily via RequestServices, not a local captured before builder.Build().
        // WebApplicationFactory<Program>'s test-time ConfigureAppConfiguration overrides only
        // land on builder.Configuration by the time Build() completes -- a value read into a
        // local earlier (during this top-level Program.cs execution) captures the pre-override
        // default and silently ignores the test's override.
        var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        var permitLimit = configuration.GetValue("RateLimiting:PermitLimit", 100);
        var windowSeconds = configuration.GetValue("RateLimiting:WindowSeconds", 60);

        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0,
        });
    });
});

// "Llm:Provider" selects the chat/vision provider (OpenAI | NvidiaNim | Azure | Anthropic | Bedrock);
// "Embeddings:Provider" optionally selects a different provider for embeddings (required whenever
// Llm:Provider is Anthropic, which has no embeddings API) and defaults to Llm:Provider otherwise.
builder.Services.AddLlmProviders(builder.Configuration);
// Model tiering (gap-closing-solutions.md Phase C, item 3): Triage's intent classification and the
// three verifiers' relevance judgments are cheap-tier calls (opt-in via *CheapChatModel config, see
// LlmServiceCollectionExtensions.CheapTierKey) -- DrafterAgent, the retrieval specialists, and
// CrossReferenceAgent stay on the default/main-tier client.
builder.Services.AddScoped<TriageAgent>(sp => new TriageAgent(
    sp.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.CheapTierKey),
    sp.GetRequiredService<ILogger<TriageAgent>>()));
builder.Services.AddScoped<FreshnessGateAgent>();
builder.Services.AddScoped<KbResearcherAgent>();
builder.Services.AddScoped<KbResearcherVerifier>(sp => new KbResearcherVerifier(
    sp.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.CheapTierKey),
    sp.GetRequiredService<ILogger<KbResearcherVerifier>>()));
builder.Services.AddScoped<CrossReferenceAgent>();
builder.Services.AddScoped<CodeAnalyzerAgent>();
builder.Services.AddScoped<CodeAnalyzerVerifier>(sp => new CodeAnalyzerVerifier(
    sp.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.CheapTierKey),
    sp.GetRequiredService<ILogger<CodeAnalyzerVerifier>>()));
builder.Services.AddScoped<VisionAnalyzerAgent>();
builder.Services.AddScoped<VisionAnalyzerVerifier>(sp => new VisionAnalyzerVerifier(
    sp.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.CheapTierKey),
    sp.GetRequiredService<ILogger<VisionAnalyzerVerifier>>()));
// C6 (gap-closing-solutions.md Phase C, item 6): wraps DrafterAgent's LLM calls with a fallback
// provider when Llm:FallbackProvider is configured -- opt-in, DrafterAgent gets the plain default
// client otherwise. Scoped to DrafterAgent's own /query path only (not ChatController.QueryStream's
// separate inline Drafter completion, which already has its own documented duplication-with-
// CoordinatorPipeline risk -- see docs/agentic-pipeline.md -- rather than adding a second thing that
// has to be hand-mirrored there).
var fallbackProviderConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Llm:FallbackProvider"]);
builder.Services.AddScoped<DrafterAgent>(sp =>
{
    var llm = fallbackProviderConfigured
        ? new FallbackLlmChatClient(
            sp.GetRequiredService<ILlmChatClient>(),
            sp.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.FallbackTierKey),
            sp.GetRequiredService<ILogger<FallbackLlmChatClient>>())
        : sp.GetRequiredService<ILlmChatClient>();
    return new DrafterAgent(llm, sp.GetRequiredService<ILogger<DrafterAgent>>());
});
builder.Services.AddScoped<KbSearchTool>();
builder.Services.AddScoped<VisionAnalysisTool>();
builder.Services.AddScoped<CoordinatorPipeline>(sp => new CoordinatorPipeline(
    sp.GetRequiredService<TriageAgent>(),
    sp.GetRequiredService<FreshnessGateAgent>(),
    sp.GetRequiredService<KbResearcherAgent>(),
    sp.GetRequiredService<KbResearcherVerifier>(),
    sp.GetRequiredService<CrossReferenceAgent>(),
    sp.GetRequiredService<CodeAnalyzerAgent>(),
    sp.GetRequiredService<CodeAnalyzerVerifier>(),
    sp.GetRequiredService<VisionAnalyzerAgent>(),
    sp.GetRequiredService<VisionAnalyzerVerifier>(),
    sp.GetRequiredService<DrafterAgent>()));
builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddSingleton<SupportForge.Agents.Tools.IIngestionActivity>(sp => sp.GetRequiredService<IngestionQueue>());
builder.Services.AddHostedService<IngestionBackgroundService>();
// C4 (gap-closing-solutions.md Phase C, item 4): periodic re-sync for KB sources with no push
// mechanism of their own -- code repos stay webhook-driven (WebhooksController.GitHub) instead.
builder.Services.AddHostedService<ScheduledKbSyncService>();
builder.Services.Configure<ConfluenceOptions>(builder.Configuration.GetSection("Confluence"));
builder.Services.AddHttpClient<ConfluencePageFetcher>();
var repoCacheRoot = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "repos");
builder.Services.AddSingleton<KbVectorIndexer>();
// Registered under its own concrete type (not just IIngestionJobFactory) so ScheduledKbSyncService
// can depend on it directly -- same singleton instance backs both registrations.
builder.Services.AddSingleton<DocumentIngestionJobFactory>(sp => new DocumentIngestionJobFactory(sp, repoCacheRoot));
builder.Services.AddSingleton<IIngestionJobFactory>(sp => sp.GetRequiredService<DocumentIngestionJobFactory>());
builder.Services.AddSingleton<GitRepoSyncService>();
builder.Services.AddSingleton<IIngestionJobFactory>(sp => new CodeIngestionJobFactory(sp, repoCacheRoot));

// Code Q&A is backed entirely by Neo4j: GraphImportJob loads each repo's code graph, tagged with
// projectId, and GraphDbQueryTool queries it over a pooled Neo4j.Driver connection.
var neo4jOptions = builder.Configuration.GetSection("Neo4j").Get<Neo4jOptions>() ?? new Neo4jOptions();
builder.Services.AddSingleton(neo4jOptions);
builder.Services.AddSingleton<Neo4j.Driver.IDriver>(_ =>
{
    var authToken = string.IsNullOrEmpty(neo4jOptions.Password)
        ? Neo4j.Driver.AuthTokens.None
        : Neo4j.Driver.AuthTokens.Basic(neo4jOptions.User, neo4jOptions.Password);
    return Neo4j.Driver.GraphDatabase.Driver(neo4jOptions.Uri, authToken, config =>
    {
        if (neo4jOptions.MaxConnectionPoolSize > 0)
            config.WithMaxConnectionPoolSize(neo4jOptions.MaxConnectionPoolSize);
    });
});
builder.Services.AddSingleton<IIngestionJobFactory>(sp => new GraphImportJobFactory(sp, repoCacheRoot));
builder.Services.AddScoped<ICodeGraphQueryTool>(sp =>
    new GraphDbQueryTool(sp.GetRequiredService<Neo4j.Driver.IDriver>(), neo4jOptions.Database));

// KTD5: replaces the bare "200 OK" /health endpoint with real per-dependency status.
builder.Services.AddHealthChecks()
    .AddCheck<VectorStoreHealthCheck>("vector_store")
    .AddCheck<LlmConnectivityHealthCheck>("llm");

// U7: traces the agent pipeline (one span per agent that ran, via PipelineTelemetry.ActivitySource
// in CoordinatorPipeline) plus inbound ASP.NET Core requests and outbound HttpClient calls.
// C5 (gap-closing-solutions.md Phase C, item 5): adds a matching metrics pipeline (request/HTTP-client
// metrics -- request rate, duration, error rate) and an optional Azure Monitor/App Insights exporter,
// additive alongside the existing OTLP/console exporters, not a replacement for either.
var appInsightsConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("SupportForge.Api"))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(PipelineTelemetry.ActivitySourceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();

        if (!string.IsNullOrEmpty(appInsightsConnectionString))
            tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConnectionString);
        else if (builder.Environment.IsDevelopment())
            tracing.AddConsoleExporter();
        else
            tracing.AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();

        if (!string.IsNullOrEmpty(appInsightsConnectionString))
            metrics.AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsightsConnectionString);
        else if (builder.Environment.IsDevelopment())
            metrics.AddConsoleExporter();
        else
            metrics.AddOtlpExporter();
    });

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), description = e.Value.Description }),
        };
        await context.Response.WriteAsJsonAsync(payload);
    },
}).AllowAnonymous();

app.Run();

namespace SupportForge.Api
{
    public partial class Program { }
}
