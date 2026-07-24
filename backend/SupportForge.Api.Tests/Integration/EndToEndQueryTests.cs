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

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Query_AfterIngestion_ReturnsAnswerCitingKbSource()
    {
        Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OpenAI__ApiKey")),
            "requires OpenAI__ApiKey and a running Chroma instance at localhost:8000");

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
