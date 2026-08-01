using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.Core;
using SupportForge.Ingestion;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Documents;
using SupportForge.Ingestion.Graphify;
using SupportForge.VectorStore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
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
// "Llm:Provider" selects the chat/vision provider (OpenAI | NvidiaNim | Azure | Anthropic | Bedrock);
// "Embeddings:Provider" optionally selects a different provider for embeddings (required whenever
// Llm:Provider is Anthropic, which has no embeddings API) and defaults to Llm:Provider otherwise.
builder.Services.AddLlmProviders(builder.Configuration);
builder.Services.AddScoped<TriageAgent>();
builder.Services.AddScoped<KbResearcherAgent>();
builder.Services.AddScoped<KbResearcherVerifier>();
builder.Services.AddScoped<CodeAnalyzerAgent>();
builder.Services.AddScoped<CodeAnalyzerVerifier>();
builder.Services.AddScoped<VisionAnalyzerAgent>();
builder.Services.AddScoped<VisionAnalyzerVerifier>();
builder.Services.AddScoped<DrafterAgent>();
builder.Services.AddScoped<KbSearchTool>();
builder.Services.AddScoped<CodeSearchTool>();
builder.Services.AddScoped<VisionAnalysisTool>();
builder.Services.AddScoped<CoordinatorPipeline>(sp => new CoordinatorPipeline(
    sp.GetRequiredService<TriageAgent>(),
    sp.GetRequiredService<KbResearcherAgent>(),
    sp.GetRequiredService<KbResearcherVerifier>(),
    sp.GetRequiredService<CodeAnalyzerAgent>(),
    sp.GetRequiredService<CodeAnalyzerVerifier>(),
    sp.GetRequiredService<VisionAnalyzerAgent>(),
    sp.GetRequiredService<VisionAnalyzerVerifier>(),
    sp.GetRequiredService<DrafterAgent>()));
builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddHostedService<IngestionBackgroundService>();
builder.Services.AddSingleton<GraphifyCliRunner>();
// Resolved eagerly (not inside a lazy DI factory) so an incompatible Llm:Provider/Graphify:Gateway
// combination (e.g. Bedrock/Azure with no gateway configured) fails at startup, not on first ingest.
var graphifyEnvironment = GraphifyBackendResolver.Resolve(builder.Configuration);
builder.Services.AddSingleton(graphifyEnvironment);
builder.Services.Configure<ConfluenceOptions>(builder.Configuration.GetSection("Confluence"));
builder.Services.AddHttpClient<ConfluencePageFetcher>();
var repoCacheRoot = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "repos");
builder.Services.AddSingleton<IIngestionJobFactory>(sp => new DocumentIngestionJobFactory(sp, repoCacheRoot));
builder.Services.AddSingleton<GitRepoSyncService>();
builder.Services.AddSingleton<IIngestionJobFactory>(sp => new CodeIngestionJobFactory(sp, repoCacheRoot));

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

namespace SupportForge.Api
{
    public partial class Program { }
}
