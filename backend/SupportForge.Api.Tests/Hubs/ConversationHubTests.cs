using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SupportForge.Agents;
using SupportForge.Api.Hubs;
using SupportForge.Core;
using SupportForge.Core.Entities;
using Xunit;

namespace SupportForge.Api.Tests.Hubs;

// U26: real SignalR clients against an in-memory TestServer (WebApplicationFactory<Program>), same
// isolation pattern AuthenticationTests.cs already established for JWT-gated integration tests --
// no in-memory transport substitute or hand-rolled hub-invocation harness.
public class ConversationHubTests : IDisposable
{
    private const string Password = "Test-Password-123!";
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "sf-hub-tests-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private WebApplicationFactory<Program> MakeFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IUserRepository>(new JsonFileUserRepository(_tempDir));
                services.AddSingleton<IConversationRepository>(new JsonFileConversationRepository(_tempDir));
                services.AddSingleton<IChatMessageRepository>(new JsonFileChatMessageRepository(_tempDir));
                services.AddSingleton<ITokenUsageRepository>(new JsonFileTokenUsageRepository(_tempDir));
                services.AddSingleton<IProjectRepository>(new JsonFileProjectRepository(_tempDir));
                services.AddSingleton<IProjectMembershipRepository>(new JsonFileProjectMembershipRepository(_tempDir));
                services.AddSingleton<ILlmChatClient>(new FakeLlmClient());
                services.AddKeyedSingleton<ILlmChatClient>(LlmServiceCollectionExtensions.CheapTierKey, (_, _) => new FakeLlmClient());
            });
        });
    }

    private static async Task<string> SeedUserAsync(WebApplicationFactory<Program> factory, string userName)
    {
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { Id = Guid.NewGuid().ToString("n"), UserName = userName };
        var result = await userManager.CreateAsync(user, Password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
        return user.Id;
    }

    private static async Task<string> GetTokenAsync(HttpClient client, string userName)
    {
        var response = await client.PostAsJsonAsync("/api/auth/token", new { UserName = userName, Password });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    private static HubConnection BuildConnection(WebApplicationFactory<Program> factory, string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/conversation"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

    // Core U26 requirement: two connected clients in one conversation's group both receive a
    // broadcast message. The broadcast itself is resolved from DI (IHubContext<ConversationHub>) --
    // the same mechanism ChatController.BroadcastMessageAsync uses after recording a turn.
    [Fact]
    public async Task TwoClientsInSameConversationGroup_BothReceiveBroadcastMessage()
    {
        using var factory = MakeFactory();
        var client = factory.CreateClient();
        var aliceId = await SeedUserAsync(factory, "alice-hub");
        var bobId = await SeedUserAsync(factory, "bob-hub");
        var aliceToken = await GetTokenAsync(client, "alice-hub");
        var bobToken = await GetTokenAsync(client, "bob-hub");

        using (var scope = factory.Services.CreateScope())
        {
            var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            await projects.UpsertAsync(new Project { Id = "proj-hub-test", Name = "Hub Test Project" });
            var memberships = scope.ServiceProvider.GetRequiredService<IProjectMembershipRepository>();
            await memberships.AddAsync(aliceId, "proj-hub-test");
            await memberships.AddAsync(bobId, "proj-hub-test");
            var conversations = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
            await conversations.UpsertAsync(new Conversation { Id = "conv-hub-test", ProjectId = "proj-hub-test", Title = "t" });
        }

        await using var aliceConnection = BuildConnection(factory, aliceToken);
        await using var bobConnection = BuildConnection(factory, bobToken);

        var aliceReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bobReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        aliceConnection.On<JsonElement>("receiveMessage", payload => aliceReceived.TrySetResult(payload.GetProperty("content").GetString()!));
        bobConnection.On<JsonElement>("receiveMessage", payload => bobReceived.TrySetResult(payload.GetProperty("content").GetString()!));

        await aliceConnection.StartAsync();
        await bobConnection.StartAsync();
        await aliceConnection.InvokeAsync("JoinConversation", "conv-hub-test");
        await bobConnection.InvokeAsync("JoinConversation", "conv-hub-test");

        using (var scope = factory.Services.CreateScope())
        {
            var hub = scope.ServiceProvider.GetRequiredService<IHubContext<ConversationHub>>();
            await hub.Clients.Group(ConversationHub.GroupName("conv-hub-test"))
                .SendAsync("receiveMessage", new { id = "m1", role = "assistant", content = "hello from the pipeline" });
        }

        var timeout = Task.Delay(TimeSpan.FromSeconds(5));
        Assert.Same(aliceReceived.Task, await Task.WhenAny(aliceReceived.Task, timeout));
        Assert.Same(bobReceived.Task, await Task.WhenAny(bobReceived.Task, timeout));
        Assert.Equal("hello from the pipeline", await aliceReceived.Task);
        Assert.Equal("hello from the pipeline", await bobReceived.Task);
    }

    [Fact]
    public async Task JoinConversation_CallerNotMemberOrInvited_Throws()
    {
        using var factory = MakeFactory();
        var client = factory.CreateClient();
        var outsiderId = await SeedUserAsync(factory, "outsider-hub");
        var outsiderToken = await GetTokenAsync(client, "outsider-hub");
        _ = outsiderId;

        using (var scope = factory.Services.CreateScope())
        {
            var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            await projects.UpsertAsync(new Project { Id = "proj-hub-locked", Name = "Locked Project" });
            var conversations = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
            await conversations.UpsertAsync(new Conversation { Id = "conv-hub-locked", ProjectId = "proj-hub-locked", Title = "t" });
        }

        await using var connection = BuildConnection(factory, outsiderToken);
        await connection.StartAsync();

        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync("JoinConversation", "conv-hub-locked"));
    }

    // U26: an invited user (not a project member) can join the group even though they'd fail the
    // plain membership check -- InvitedUserIds is the second half of the effective-access OR.
    [Fact]
    public async Task JoinConversation_InvitedButNotProjectMember_Succeeds()
    {
        using var factory = MakeFactory();
        var client = factory.CreateClient();
        var invitedId = await SeedUserAsync(factory, "invited-hub");
        var invitedToken = await GetTokenAsync(client, "invited-hub");

        using (var scope = factory.Services.CreateScope())
        {
            var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            await projects.UpsertAsync(new Project { Id = "proj-hub-invite", Name = "Invite Project" });
            var conversations = scope.ServiceProvider.GetRequiredService<IConversationRepository>();
            await conversations.UpsertAsync(new Conversation
            {
                Id = "conv-hub-invite", ProjectId = "proj-hub-invite", Title = "t", InvitedUserIds = new List<string> { invitedId },
            });
        }

        await using var connection = BuildConnection(factory, invitedToken);
        await connection.StartAsync();

        var exception = await Record.ExceptionAsync(() => connection.InvokeAsync("JoinConversation", "conv-hub-invite"));
        Assert.Null(exception);
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

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default, EmbeddingPurpose purpose = EmbeddingPurpose.Query)
            => Task.FromResult(new float[] { 0.1f });
    }
}
