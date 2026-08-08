using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SupportForge.Api.Contracts;
using SupportForge.Api.Identity;
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

        // U4 gates every controller here behind [Authorize] -- seed a user and attach its token.
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var existing = await userManager.FindByNameAsync("e2e-tester");
            if (existing is null)
                await userManager.CreateAsync(new AppUser { Id = "e2e-tester", UserName = "e2e-tester" }, "Test-Password-123!");
        }
        var tokenResponse = await client.PostAsJsonAsync("/api/auth/token", new { UserName = "e2e-tester", Password = "Test-Password-123!" });
        tokenResponse.EnsureSuccessStatusCode();
        var accessToken = (await tokenResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var project = new Project { OrgId = "test-org",
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
        var raw = await response.Content.ReadAsStringAsync();
        var body = System.Text.Json.JsonSerializer.Deserialize<ChatQueryResponse>(
            raw, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.NotNull(body);
        Assert.Contains("password", body!.Draft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".md", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".cs", raw, StringComparison.OrdinalIgnoreCase);
    }
}
