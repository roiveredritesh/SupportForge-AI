using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
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
using SupportForge.Ingestion.Graphify;
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
builder.Services.AddSingleton<IFeedbackRepository>(
    new JsonFileFeedbackRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
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
builder.Services.AddScoped<TriageAgent>();
builder.Services.AddScoped<FreshnessGateAgent>();
builder.Services.AddScoped<KbResearcherAgent>();
builder.Services.AddScoped<KbResearcherVerifier>();
builder.Services.AddScoped<CrossReferenceAgent>();
builder.Services.AddScoped<CodeAnalyzerAgent>();
builder.Services.AddScoped<CodeAnalyzerVerifier>();
builder.Services.AddScoped<VisionAnalyzerAgent>();
builder.Services.AddScoped<VisionAnalyzerVerifier>();
builder.Services.AddScoped<DrafterAgent>();
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
// Graphify:CliConcurrency: max concurrent `graphify` subprocesses (default 2, matching the tool's
// prior hardcoded value). Raise this once ingestion throughput, not CPU/LLM rate limits, is the
// bottleneck -- the subprocess gate is independent of GraphDbQueryTool's Neo4j connection pool, which
// serves query traffic and isn't affected by this setting.
var graphifyCliConcurrency = builder.Configuration.GetValue("Graphify:CliConcurrency", 2);
builder.Services.AddSingleton(sp => new GraphifyCliRunner(sp.GetRequiredService<ILogger<GraphifyCliRunner>>(), concurrency: graphifyCliConcurrency));
// Resolved eagerly (not inside a lazy DI factory) so an incompatible Llm:Provider/Graphify:Gateway
// combination (e.g. Bedrock/Azure with no gateway configured) fails at startup, not on first ingest.
var graphifyEnvironment = GraphifyBackendResolver.Resolve(builder.Configuration);
builder.Services.AddSingleton(graphifyEnvironment);
builder.Services.Configure<ConfluenceOptions>(builder.Configuration.GetSection("Confluence"));
builder.Services.AddHttpClient<ConfluencePageFetcher>();
var repoCacheRoot = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "repos");
builder.Services.AddSingleton<KbVectorIndexer>();
builder.Services.AddSingleton<IIngestionJobFactory>(sp => new DocumentIngestionJobFactory(sp, repoCacheRoot));
builder.Services.AddSingleton<GitRepoSyncService>();
builder.Services.AddSingleton<IIngestionJobFactory>(sp => new CodeIngestionJobFactory(sp, repoCacheRoot));
// Registered immediately after CodeIngestionJobFactory: IngestionController.Trigger enqueues jobs by
// iterating IIngestionJobFactory in registration order, and the queue drains strictly FIFO, so every
// per-repo CodeIngestionJob for a project is guaranteed to run before that project's merge job.
builder.Services.AddSingleton<IIngestionJobFactory>(sp => new CodeGraphMergeJobFactory(sp, repoCacheRoot));

// WS1 (retrieval-pipeline remediation plan): cut over ahead of the parity spike per explicit product
// direction -- GraphifyQueryTool's hand-rolled SemaphoreSlim(2,2) subprocess gate doesn't scale with
// concurrent query traffic; GraphDbQueryTool goes through Neo4j.Driver's connection pool instead.
// GraphifyCliRunner (and the `graphify` CLI itself) stays: it's still the only thing that extracts a
// code graph from source in the first place -- GraphImportJob feeds its output into Neo4j. Only the
// query-time path changes.
var neo4jOptions = builder.Configuration.GetSection("Graphify:Neo4j").Get<Neo4jOptions>() ?? new Neo4jOptions();
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
builder.Services.AddScoped<IGraphifyQueryTool>(sp =>
    new GraphDbQueryTool(sp.GetRequiredService<Neo4j.Driver.IDriver>(), neo4jOptions.Database));

// KTD5: replaces the bare "200 OK" /health endpoint with real per-dependency status.
builder.Services.AddHealthChecks()
    .AddCheck<VectorStoreHealthCheck>("vector_store")
    .AddCheck<LlmConnectivityHealthCheck>("llm")
    .AddCheck<GraphifyHealthCheck>("graphify");

// U7: traces the agent pipeline (one span per agent that ran, via PipelineTelemetry.ActivitySource
// in CoordinatorPipeline) plus inbound ASP.NET Core requests and outbound HttpClient calls.
// Exporter target is environment-driven (standard OTEL_EXPORTER_OTLP_ENDPOINT env var); falls back
// to the console exporter in Development so tracing is visible with zero collector setup.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("SupportForge.Api"))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(PipelineTelemetry.ActivitySourceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();

        if (builder.Environment.IsDevelopment())
        {
            tracing.AddConsoleExporter();
        }
        else
        {
            tracing.AddOtlpExporter();
        }
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
