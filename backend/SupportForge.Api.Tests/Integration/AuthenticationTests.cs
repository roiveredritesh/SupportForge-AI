using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SupportForge.Agents;
using SupportForge.Api.Contracts;
using SupportForge.Api.Identity;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Integration;

// Exercises the U4 auth/rate-limiting surface end-to-end (via WebApplicationFactory<Program>,
// following EndToEndQueryTests.cs's pattern): token issuance, JWT-gated controllers, and the
// built-in RateLimiter middleware.
public class AuthenticationTests : IDisposable
{
    private const string Password = "Test-Password-123!";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "sf-auth-tests-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // Isolates JSON-file-backed state (users, conversations, chat messages, token usage) to a temp
    // directory instead of the real App_Data, and swaps in a fake LLM so /api/chat/query can
    // complete without a real provider/network call -- ChatControllerTests already covers the
    // pipeline's business logic directly; this only needs the request to clear the auth gate.
    private WebApplicationFactory<Program> MakeFactory(Action<IServiceCollection>? extraServices = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IUserRepository>(new JsonFileUserRepository(_tempDir));
                services.AddSingleton<IConversationRepository>(new JsonFileConversationRepository(_tempDir));
                services.AddSingleton<IChatMessageRepository>(new JsonFileChatMessageRepository(_tempDir));
                services.AddSingleton<ITokenUsageRepository>(new JsonFileTokenUsageRepository(_tempDir));
                services.AddSingleton<ILlmChatClient>(new FakeLlmClient());
                extraServices?.Invoke(services);
            });
        });
    }

    private static async Task<string> SeedUserAsync(WebApplicationFactory<Program> factory, string userName = "alice", string password = Password)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { Id = Guid.NewGuid().ToString("n"), UserName = userName };
        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    private static async Task<string> GetTokenAsync(HttpClient client, string userName = "alice", string password = Password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/token", new { UserName = userName, Password = password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    private static string BuildCustomToken(IConfiguration configuration, string issuer, string audience, TimeSpan expiresIn, string sub = "some-user")
    {
        var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, sub) };
        var credentials = new SigningCredentials(JwtTokenFactory.ResolveSigningKey(configuration), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(issuer, audience, claims, expires: DateTime.UtcNow.Add(expiresIn), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpRequestMessage ChatQueryRequestMessage() => new(HttpMethod.Post, "/api/chat/query")
    {
        Content = JsonContent.Create(new ChatQueryRequest { ProjectId = "proj-auth-test", Query = "hello" }),
    };

    [Fact]
    public async Task Token_WithValidCredentials_ReturnsSignedJwt()
    {
        using var factory = MakeFactory();
        var client = factory.CreateClient();
        await SeedUserAsync(factory);

        var response = await client.PostAsJsonAsync("/api/auth/token", new { UserName = "alice", Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrEmpty(token));
        Assert.Equal(3, token!.Split('.').Length); // header.payload.signature
    }

    [Fact]
    public async Task Token_WithWrongCredentials_ReturnsUnauthorized()
    {
        using var factory = MakeFactory();
        var client = factory.CreateClient();
        await SeedUserAsync(factory);

        var response = await client.PostAsJsonAsync("/api/auth/token", new { UserName = "alice", Password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidJwt_ReachesChatController_AndSucceeds()
    {
        using var factory = MakeFactory();
        var client = factory.CreateClient();
        await SeedUserAsync(factory);
        var token = await GetTokenAsync(client);

        var request = ChatQueryRequestMessage();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request);

        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)response.StatusCode}: {text}");
    }

    [Fact]
    public async Task NoAuthorizationHeader_ReturnsUnauthorizedNotServerError()
    {
        using var factory = MakeFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(ChatQueryRequestMessage());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MalformedBearerToken_ReturnsUnauthorized()
    {
        using var factory = MakeFactory();
        var client = factory.CreateClient();

        var request = ChatQueryRequestMessage();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-jwt");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ExpiredToken_ReturnsUnauthorized()
    {
        using var factory = MakeFactory();
        var client = factory.CreateClient();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var expired = BuildCustomToken(
            configuration, JwtTokenFactory.ResolveIssuer(configuration), JwtTokenFactory.ResolveAudience(configuration), TimeSpan.FromMinutes(-5));

        var request = ChatQueryRequestMessage();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", expired);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongAudienceAndIssuer_ReturnsUnauthorized()
    {
        using var factory = MakeFactory();
        var client = factory.CreateClient();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var wrongAudience = BuildCustomToken(configuration, JwtTokenFactory.ResolveIssuer(configuration), "some-other-audience", TimeSpan.FromHours(1));

        var request = ChatQueryRequestMessage();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wrongAudience);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task HealthAndTokenEndpoint_RemainReachableWithoutToken()
    {
        using var factory = MakeFactory();
        var client = factory.CreateClient();

        // /health is reachable without a token (not 401) -- its actual status (200/503) depends on
        // whether the test host's VectorStore/LLM/graph-db dependencies are live, which they are not
        // in this in-memory test environment (U6 added real per-dependency checks).
        var health = await client.GetAsync("/health");
        Assert.NotEqual(HttpStatusCode.Unauthorized, health.StatusCode);

        // Wrong credentials still reach the endpoint (401 from the handler, not a 401 from auth
        // middleware blocking the anonymous route itself).
        var token = await client.PostAsJsonAsync("/api/auth/token", new { UserName = "nobody", Password = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, token.StatusCode);
    }

    [Fact]
    public async Task ExceedingRateLimit_Returns429_AndResetsAfterWindow()
    {
        // Program.cs reads RateLimiting:PermitLimit/WindowSeconds once at startup from
        // configuration -- override them here (before the host builds) so the limit is small
        // enough to exhaust and reset within a fast test.
        using var lowLimitFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:PermitLimit"] = "2",
                    ["RateLimiting:WindowSeconds"] = "1",
                });
            });
        });
        var client = lowLimitFactory.CreateClient();

        // Hits /api/auth/token (anonymous, no external I/O -- just a fast password-hash mismatch)
        // rather than /health: since U6, /health does real bounded-but-nonzero dependency I/O per
        // request, which made this tight 1-second window flaky regardless of timeout tuning.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/token", new { UserName = "nobody", Password = "x" });
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);

        await Task.Delay(TimeSpan.FromSeconds(1.5));

        var afterWindow = await client.PostAsJsonAsync("/api/auth/token", new { UserName = "nobody", Password = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, afterWindow.StatusCode); // reachable again, not 429
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        public bool SupportsVision => false;
        public int LastTotalTokens => 0;

        public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
            => Task.FromResult("unclear");

        public async IAsyncEnumerable<string> StreamCompleteAsync(
            string systemPrompt, string userPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return "unclear";
            await Task.CompletedTask;
        }

        public Task<string> AnalyzeImageAsync(string base64Image, string prompt, CancellationToken ct = default)
            => Task.FromResult(string.Empty);

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult(new float[] { 0.1f });
    }
}
