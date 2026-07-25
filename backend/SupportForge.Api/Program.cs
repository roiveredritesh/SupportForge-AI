using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.Core;
using SupportForge.Ingestion;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Documents;
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
var llmBaseUrl = builder.Configuration["Llm:BaseUrl"] ?? "https://api.openai.com/v1/";
var llmApiKey = builder.Configuration["Llm:ApiKey"] ?? builder.Configuration["OpenAI:ApiKey"];
var llmChatModel = builder.Configuration["Llm:ChatModel"] ?? "gpt-4o-mini";
var llmEmbeddingModel = builder.Configuration["Llm:EmbeddingModel"] ?? "text-embedding-3-small";
var llmEmbeddingInputType = builder.Configuration["Llm:EmbeddingInputType"];
builder.Services.AddHttpClient("Llm", client =>
{
    client.BaseAddress = new Uri(llmBaseUrl);
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {llmApiKey}");
}).AddTypedClient<ILlmClient>((client, _) => new OpenAiLlmClient(client, llmChatModel, llmEmbeddingModel, llmEmbeddingInputType));
builder.Services.AddScoped<TriageAgent>();
builder.Services.AddScoped<KbResearcherAgent>();
builder.Services.AddScoped<CodeAnalyzerAgent>();
builder.Services.AddScoped<VisionAnalyzerAgent>();
builder.Services.AddScoped<DrafterAgent>();
builder.Services.AddScoped<KbSearchTool>();
builder.Services.AddScoped<CodeSearchTool>();
builder.Services.AddScoped<VisionAnalysisTool>();
builder.Services.AddScoped<CoordinatorPipeline>(sp => new CoordinatorPipeline(new IAgent[]
{
    sp.GetRequiredService<TriageAgent>(),
    sp.GetRequiredService<KbResearcherAgent>(),
    sp.GetRequiredService<CodeAnalyzerAgent>(),
    sp.GetRequiredService<VisionAnalyzerAgent>(),
    sp.GetRequiredService<DrafterAgent>(),
}));
builder.Services.AddSingleton<IngestionQueue>();
builder.Services.AddHostedService<IngestionBackgroundService>();
builder.Services.AddSingleton<IIngestionJobFactory>(sp => new DocumentIngestionJobFactory(sp));
builder.Services.AddSingleton<GitRepoSyncService>();
builder.Services.AddSingleton<IIngestionJobFactory>(sp => new CodeIngestionJobFactory(
    sp,
    Path.Combine(builder.Environment.ContentRootPath, "App_Data", "repos")));

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
