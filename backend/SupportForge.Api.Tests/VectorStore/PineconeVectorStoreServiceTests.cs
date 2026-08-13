using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using SupportForge.VectorStore.Models;
using SupportForge.VectorStore.Pinecone;
using Xunit;

namespace SupportForge.Api.Tests.VectorStore;

public class PineconeVectorStoreServiceTests
{
    private static (Mock<HttpMessageHandler> handler, PineconeVectorStoreService sut) MakeSut(float? minScore = null)
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var client = new HttpClient(handler.Object) { BaseAddress = new Uri("https://test-index.pinecone.io") };
        var options = Options.Create(new PineconeOptions { Host = "https://test-index.pinecone.io", ApiKey = "test-key", MinScore = minScore });
        return (handler, new PineconeVectorStoreService(client, options));
    }

    [Fact]
    public async Task UpsertAsync_StashesTextUnderReservedMetadataKey()
    {
        var (handler, sut) = MakeSut();
        JsonElement? capturedBody = null;
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.EndsWith("/vectors/upsert")),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) =>
                capturedBody = JsonSerializer.Deserialize<JsonElement>(req.Content!.ReadAsStringAsync().Result))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        var doc = new VectorDocument("doc-1", "hello world", new float[] { 0.1f, 0.2f }, new Dictionary<string, string> { ["source"] = "kb/x.md" });
        await sut.UpsertAsync("proj1-kb", new[] { doc });

        Assert.NotNull(capturedBody);
        var vector = capturedBody!.Value.GetProperty("vectors")[0];
        Assert.Equal("doc-1", vector.GetProperty("id").GetString());
        var metadata = vector.GetProperty("metadata");
        Assert.Equal("hello world", metadata.GetProperty("_text").GetString());
        Assert.Equal("kb/x.md", metadata.GetProperty("source").GetString());
        Assert.Equal("proj1-kb", capturedBody!.Value.GetProperty("namespace").GetString());
    }

    [Fact]
    public async Task QueryAsync_ExtractsTextBackOutOfMetadata_AndDoesNotLeakReservedKey()
    {
        var (handler, sut) = MakeSut();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.EndsWith("/query")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "matches": [
                    { "id": "doc-1", "score": 0.87, "metadata": { "_text": "hello world", "source": "kb/x.md" } }
                  ]
                }
                """)
            });

        var results = await sut.QueryAsync("proj1-kb", new float[] { 0.1f, 0.2f }, topK: 1);

        Assert.Single(results);
        Assert.Equal("doc-1", results[0].Id);
        Assert.Equal("hello world", results[0].Text);
        Assert.Equal("kb/x.md", results[0].Metadata["source"]);
        Assert.False(results[0].Metadata.ContainsKey("_text"));
    }

    private static void SetupQueryResponse(Mock<HttpMessageHandler> handler)
    {
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.EndsWith("/query")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "matches": [
                    { "id": "doc-1", "score": 0.87, "metadata": { "_text": "hello world", "source": "kb/x.md" } },
                    { "id": "doc-2", "score": 0.20, "metadata": { "_text": "goodbye world", "source": "kb/y.md" } }
                  ]
                }
                """)
            });
    }

    [Fact]
    public async Task QueryAsync_ReturnsAllTopKMatches_WhenMinScoreUnset()
    {
        var (handler, sut) = MakeSut(minScore: null);
        SetupQueryResponse(handler);

        var results = await sut.QueryAsync("proj1-kb", new float[] { 0.1f, 0.2f }, topK: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task QueryAsync_ExcludesMatchesBelowMinScore()
    {
        var (handler, sut) = MakeSut(minScore: 0.5f);
        SetupQueryResponse(handler);

        var results = await sut.QueryAsync("proj1-kb", new float[] { 0.1f, 0.2f }, topK: 2);

        Assert.Single(results);
        Assert.Equal("doc-1", results[0].Id);
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmptyList_WhenNoMatchQualifiesUnderMinScore()
    {
        var (handler, sut) = MakeSut(minScore: 0.95f);
        SetupQueryResponse(handler);

        var results = await sut.QueryAsync("proj1-kb", new float[] { 0.1f, 0.2f }, topK: 2);

        Assert.Empty(results);
    }

    [Fact]
    public async Task CountAsync_ReturnsVectorCountForNamespace()
    {
        var (handler, sut) = MakeSut();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.EndsWith("/describe_index_stats")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "namespaces": { "proj1-kb": { "vectorCount": 42 } } }""")
            });

        Assert.Equal(42, await sut.CountAsync("proj1-kb"));
    }

    [Fact]
    public async Task CountAsync_ReturnsZero_WhenNamespaceAbsentFromStats()
    {
        var (handler, sut) = MakeSut();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.EndsWith("/describe_index_stats")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "namespaces": { "some-other-project-kb": { "vectorCount": 5 } } }""")
            });

        Assert.Equal(0, await sut.CountAsync("proj-with-no-data-yet"));
    }
}
