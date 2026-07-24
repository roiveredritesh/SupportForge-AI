using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.VectorStore;

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
builder.Services.AddVectorStore(builder.Configuration);
builder.Services.AddSingleton<IProjectRepository>(
    new JsonFileProjectRepository(Path.Combine(builder.Environment.ContentRootPath, "App_Data")));
builder.Services.AddHttpClient<ILlmClient, OpenAiLlmClient>(client =>
{
    client.BaseAddress = new Uri("https://api.openai.com/v1/");
    client.DefaultRequestHeaders.Add("Authorization", $"Bearer {builder.Configuration["OpenAI:ApiKey"]}");
});
builder.Services.AddScoped<TriageAgent>();

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
