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
using SupportForge.Agents.Guard;
using SupportForge.Agents.Jev;
using SupportForge.Agents.Tools;
using SupportForge.Agents.Triage;
using SupportForge.Agents.Verification;
using SupportForge.Api;
using SupportForge.Api.HealthChecks;
using SupportForge.Api.Hubs;
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
builder.Services.AddSingleton<IOrgRepository>(
    new JsonFileOrgRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
builder.Services.AddSingleton<IOrgMembershipRepository>(
    new JsonFileOrgMembershipRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
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
builder.Services.AddSingleton<IEscalationRepository>(
    new JsonFileEscalationRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
builder.Services.AddSingleton<IUserRepository>(
    new JsonFileUserRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));

// U26: ConversationHub -- part of the Microsoft.AspNetCore.App shared framework this API already
// targets (net9.0 Web SDK), no external PackageReference needed. IConversationPresenceTracker backs
// PresenceIndicator's roster (join/leave broadcasts), see Hubs/ConversationPresenceTracker.cs.
builder.Services.AddSignalR();
builder.Services.AddSingleton<IConversationPresenceTracker, ConversationPresenceTracker>();

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
        // U26: browsers can't set an Authorization header on a WebSocket upgrade request, so the
        // SignalR JS client sends the token as an "access_token" query param instead (standard
        // ASP.NET Core SignalR JWT pattern) -- only honored on the hub's own path, every other
        // endpoint still requires the real Authorization header.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments("/hubs/conversation"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
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
// U16/U17: every Jev-backed feature shares this one registration. It always binds JevOptions, and it
// registers IJevClient only when Jev:ApiKey is set, so with the key blank every Jev-backed feature is inert.
// Declared here, before TriageAgent, because the Triage registration below reads it.
var jevConfigured = builder.Services.AddJev(builder.Configuration);
// U6 (JEV intent classification plan), R7/R12: Triage:Classifier picks the intent classifier. Llm (the default) needs no Jev
// key. Jev and Shadow both need one, so a missing key fails at startup rather than on every request.
var triageOptions = builder.Configuration.GetSection("Triage").Get<TriageOptions>() ?? new TriageOptions();
builder.Services.AddSingleton(triageOptions);
if (triageOptions.Classifier != TriageClassifierMode.Llm && !jevConfigured)
{
    throw new InvalidOperationException(
        $"Triage:Classifier is {triageOptions.Classifier} but Jev is not configured (Jev:ApiKey is empty). Set Jev:ApiKey or use Triage:Classifier=Llm.");
}
// U12/U14 (JEV findings validation plan): Verification:Judge picks the findings judge behind the KB, code and vision verifiers and
// the Drafter's groundedness check. Llm (the default) needs no Jev key. Jev and Shadow need one, so a missing key fails at startup.
var verificationOptions = builder.Configuration.GetSection("Verification").Get<VerificationOptions>() ?? new VerificationOptions();
// U18 (indirect injection, R26): one drop threshold is shared by the guard and the findings verifiers, so it is read from the Guard section.
verificationOptions.IndirectThreshold = builder.Configuration.GetValue("Guard:IndirectThreshold", verificationOptions.IndirectThreshold);
builder.Services.AddSingleton(verificationOptions);
if (verificationOptions.Judge != FindingsJudgeMode.Llm && !jevConfigured)
{
    throw new InvalidOperationException(
        $"Verification:Judge is {verificationOptions.Judge} but Jev is not configured (Jev:ApiKey is empty). Set Jev:ApiKey or use Verification:Judge=Llm.");
}
// U17 / KTD 13: bound once, here, because the output guard singleton (U19) and the guard registrations below both read it.
// Guard:CombineWithTriage shares one Jev request between the guard and triage, so it needs Triage:Classifier=Jev and a Jev key.
var guardOptions = builder.Configuration.GetSection("Guard").Get<GuardOptions>() ?? new GuardOptions();
if (guardOptions.CombineWithTriage && (triageOptions.Classifier != TriageClassifierMode.Jev || !jevConfigured))
{
    throw new InvalidOperationException(
        $"Guard:CombineWithTriage needs Triage:Classifier=Jev and a Jev key (Jev:ApiKey). Triage:Classifier is {triageOptions.Classifier} and Jev is {(jevConfigured ? "configured" : "not configured")}. Set both, or set Guard:CombineWithTriage=false.");
}
// U12: the LLM judge always answers, and the Jev judge sits behind FindingsJudgeSelector, which only consults it in the modes that
// need it. Declared here, before the verifier and Drafter registrations below that call it.
IFindingsJudge FindingsJudge(IServiceProvider sp, ILlmChatClient llm) =>
    new FindingsJudgeSelector(
        new LlmFindingsJudge(llm),
        jevConfigured ? new JevFindingsJudge(sp.GetRequiredService<IJevClient>(), verificationOptions) : null,
        verificationOptions,
        sp.GetRequiredService<ILogger<FindingsJudgeSelector>>());
// Model tiering (gap-closing-solutions.md Phase C, item 3): Triage's intent classification and the
// three verifiers' relevance judgments are cheap-tier calls (opt-in via *CheapChatModel config, see
// LlmServiceCollectionExtensions.CheapTierKey) -- DrafterAgent, the retrieval specialists, and
// CrossReferenceAgent stay on the default/main-tier client.
builder.Services.AddScoped<TriageAgent>(sp => new TriageAgent(
    new LlmIntentClassifier(sp.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.CheapTierKey)),
    jevConfigured ? new JevIntentClassifier(sp.GetRequiredService<IJevClient>(), triageOptions) : null,
    triageOptions,
    sp.GetRequiredService<ILogger<TriageAgent>>()));
builder.Services.AddScoped<FreshnessGateAgent>();
builder.Services.AddScoped<KbResearcherAgent>();
builder.Services.AddScoped<KbResearcherVerifier>(sp => new KbResearcherVerifier(
    FindingsJudge(sp, sp.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.CheapTierKey)),
    verificationOptions,
    sp.GetRequiredService<ILogger<KbResearcherVerifier>>()));
builder.Services.AddScoped<CrossReferenceAgent>();
builder.Services.AddScoped<CodeAnalyzerAgent>();
builder.Services.AddScoped<CodeAnalyzerVerifier>(sp => new CodeAnalyzerVerifier(
    FindingsJudge(sp, sp.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.CheapTierKey)),
    verificationOptions,
    sp.GetRequiredService<ILogger<CodeAnalyzerVerifier>>()));
builder.Services.AddScoped<VisionAnalyzerAgent>();
builder.Services.AddScoped<VisionAnalyzerVerifier>(sp => new VisionAnalyzerVerifier(
    FindingsJudge(sp, sp.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.CheapTierKey)),
    verificationOptions,
    sp.GetRequiredService<ILogger<VisionAnalyzerVerifier>>()));
// C6 (gap-closing-solutions.md Phase C, item 6): wraps DrafterAgent's LLM calls with a fallback
// provider when Llm:FallbackProvider is configured -- opt-in, DrafterAgent gets the plain default
// client otherwise. Scoped to DrafterAgent's own /query path only (not ChatController.QueryStream's
// separate inline Drafter completion, which already has its own documented duplication-with-
// CoordinatorPipeline risk -- see docs/agentic-pipeline.md -- rather than adding a second thing that
// has to be hand-mirrored there).
var fallbackProviderConfigured = !string.IsNullOrWhiteSpace(builder.Configuration["Llm:FallbackProvider"]);
// D3 (gap-closing-solutions.md Phase D, item 3): opt-in, unvalidated against real model output in
// this session -- see DrafterAgent's own comment on _groundednessCheckEnabled.
var groundednessCheckEnabled = builder.Configuration.GetValue<bool>("Drafter:GroundednessCheckEnabled");
builder.Services.AddScoped<DrafterAgent>(sp =>
{
    var llm = fallbackProviderConfigured
        ? new FallbackLlmChatClient(
            sp.GetRequiredService<ILlmChatClient>(),
            sp.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.FallbackTierKey),
            sp.GetRequiredService<ILogger<FallbackLlmChatClient>>())
        : sp.GetRequiredService<ILlmChatClient>();
    return new DrafterAgent(
        llm,
        sp.GetRequiredService<ILogger<DrafterAgent>>(),
        groundednessCheckEnabled,
        judge: FindingsJudge(sp, llm),
        outputGuard: sp.GetRequiredService<OutputGuardService>());
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
// U17: input guard, screened by ChatController before the pipeline runs. Off by default (Guard:Mode in
// appsettings.json), so these registrations cost nothing until a mode is set.
builder.Services.AddSingleton(guardOptions);
builder.Services.AddSingleton(new GuardRepeatTracker(TimeProvider.System));
builder.Services.AddSingleton(sp => new HeuristicInputGuard(sp.GetRequiredService<GuardOptions>()));
// U16: the Jev guard is wired only when Jev is configured. Without it the guard runs heuristic-only,
// which is flag-only by design. U17 / KTD 13: with Guard:CombineWithTriage, the same Jev request also carries the triage answer,
// so TurnAssessor replaces JevInputGuard.
IInputGuard? JevTurnGuard(IServiceProvider sp)
{
    if (!jevConfigured) return null;

    var jev = sp.GetRequiredService<IJevClient>();
    if (guardOptions.CombineWithTriage)
    {
        return new TurnAssessor(jev, guardOptions, triageOptions);
    }

    return new JevInputGuard(jev, guardOptions);
}
builder.Services.AddSingleton(sp => new GuardService(
    sp.GetRequiredService<GuardOptions>(), sp.GetRequiredService<HeuristicInputGuard>(),
    JevTurnGuard(sp),
    sp.GetRequiredService<GuardRepeatTracker>(), sp.GetRequiredService<ILogger<GuardService>>()));
// U19 (KTD 15 / R27): the output guard checks every final draft for disclosure and for obeying retrieved content. One singleton serves
// DrafterAgent and ChatController's streaming path, so the two can't drift. Off by default, through Guard:Mode.
builder.Services.AddSingleton(sp => new OutputGuardService(
    guardOptions,
    jevConfigured ? new JevOutputGuard(sp.GetRequiredService<IJevClient>(), guardOptions, verificationOptions.Jev.IncludeCode) : null,
    sp.GetRequiredService<ILogger<OutputGuardService>>()));
builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddSingleton<SupportForge.Agents.Tools.IIngestionActivity>(sp => sp.GetRequiredService<IngestionQueue>());
builder.Services.AddHostedService<IngestionBackgroundService>();
// C4 (gap-closing-solutions.md Phase C, item 4): periodic re-sync for KB sources with no push
// mechanism of their own -- code repos stay webhook-driven (WebhooksController.GitHub) instead.
builder.Services.AddHostedService<ScheduledKbSyncService>();
builder.Services.Configure<ConfluenceOptions>(builder.Configuration.GetSection("Confluence"));
// Singleton, not scoped: AddHttpClient<ConfluencePageFetcher>()'s typed-client factory resolves its
// constructor dependencies from the root provider, which cannot see scoped services and throws
// "Cannot resolve scoped service from root provider" at request time. IngestionImageCaptioner holds
// only an ILlmChatClient (itself singleton), so it has no scoped state to lose.
builder.Services.AddSingleton<IngestionImageCaptioner>();
// Credential-free client for editor-supplied external image/diagram URLs (KTD10) -- must never carry
// the Confluence bearer token set on the AddHttpClient<ConfluencePageFetcher> client below.
builder.Services.AddHttpClient("ConfluenceExternalImages");
builder.Services.AddHttpClient<ConfluencePageFetcher>();
var repoCacheRoot = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "repos");
builder.Services.AddSingleton<KbVectorIndexer>();
// Registered under its own concrete type (not just IIngestionJobFactory) so ScheduledKbSyncService
// can depend on it directly -- same singleton instance backs both registrations.
builder.Services.AddSingleton<DocumentIngestionJobFactory>(sp => new DocumentIngestionJobFactory(sp, repoCacheRoot));
builder.Services.AddSingleton<IIngestionJobFactory>(sp => sp.GetRequiredService<DocumentIngestionJobFactory>());
builder.Services.AddSingleton<GitRepoSyncService>();
builder.Services.AddSingleton<IIngestionJobFactory>(sp => new CodeIngestionJobFactory(sp, repoCacheRoot));
// U11: CommitLookupTool -- named HttpClient for its standalone GitHub REST calls (PR/issue link
// resolution), reusing the same GitHub:Token config GitRepoSyncService already reads. Singleton,
// same reasoning as ConfluencePageFetcher/IngestionImageCaptioner above.
builder.Services.AddHttpClient("GitHubApi");
builder.Services.AddSingleton<CommitLookupTool>(sp => new CommitLookupTool(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("GitHubApi"),
    builder.Configuration["GitHub:Token"],
    repoCacheRoot));

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
// U24: same pooled driver, separate query mode (calls_endpoint traversal instead of fulltext + BFS).
builder.Services.AddScoped<IBlastRadiusQueryTool>(sp =>
    new BlastRadiusQueryTool(sp.GetRequiredService<Neo4j.Driver.IDriver>(), neo4jOptions.Database));

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

// U17: until a Jev guard is wired in, Shadow and Enforce only ever flag -- say so once at startup so
// an operator who set Guard:Mode doesn't assume turns are being blocked.
if (guardOptions.Mode != GuardMode.Off && !jevConfigured)
{
    app.Logger.LogWarning(
        "Input guard mode is {Mode} but no Jev guard is configured: the guard runs heuristic-only, which can flag but never block.",
        guardOptions.Mode);
}

// U16: with Jev configured, the guard is the full two-layer guard. Logged once so the mode in effect is visible.
if (guardOptions.Mode != GuardMode.Off && jevConfigured)
{
    app.Logger.LogInformation("Input guard mode is {Mode} with the Jev guard", guardOptions.Mode);
}

// Sprint 7: must run before ProjectOrgMigration (or any other IOrgRepository consumer) ever reads
// orgs.json through the strict Org model -- see OrgRequiredFieldsMigration's own comment for why.
await OrgRequiredFieldsMigration.RunAsync(Path.Combine(builder.Environment.ContentRootPath, "App_Data"));

// Sprint 0 (U2): must run before the app starts serving requests so no request can observe a
// project with a missing OrgId.
await ProjectOrgMigration.RunAsync(
    app.Services.GetRequiredService<IProjectRepository>(),
    app.Services.GetRequiredService<IOrgRepository>());

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
app.MapHub<ConversationHub>("/hubs/conversation");
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
