using System.Net;
using Microsoft.Extensions.Options;
using Moq;
using Moq.Protected;
using SupportForge.VectorStore.Chroma;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.VectorStore;

public class ChromaVectorStoreServiceTests
{
    private static (Mock<HttpMessageHandler> handler, ChromaVectorStoreService sut) MakeSut(float? maxDistance = null)
    {
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.EndsWith("/collections")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "id": "collection-guid", "name": "proj1-kb" }""")
            });
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsolutePath.EndsWith("/query")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "ids": [["doc-1", "doc-2"]],
                  "documents": [["hello world", "goodbye world"]],
                  "distances": [[0.12, 0.9]],
                  "metadatas": [[{"source": "kb"}, {"source": "kb2"}]]
                }
                """)
            });

        var client = new HttpClient(handler.Object) { BaseAddress = new Uri("http://localhost:8000") };
        var options = Options.Create(new ChromaOptions { BaseUrl = "http://localhost:8000", MaxDistance = maxDistance });
        return (handler, new ChromaVectorStoreService(client, options));
    }

    [Fact]
    public async Task QueryAsync_ParsesChromaResponse_IntoVectorQueryResults()
    {
        var (_, sut) = MakeSut();

        var results = await sut.QueryAsync("proj1-kb", new float[] { 0.1f, 0.2f }, topK: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("doc-1", results[0].Id);
        Assert.Equal("hello world", results[0].Text);
        Assert.Equal("kb", results[0].Metadata["source"]);
    }

    [Fact]
    public async Task QueryAsync_ReturnsAllTopKResults_WhenMaxDistanceUnset()
    {
        var (_, sut) = MakeSut(maxDistance: null);

        var results = await sut.QueryAsync("proj1-kb", new float[] { 0.1f, 0.2f }, topK: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task QueryAsync_ExcludesResultsBeyondMaxDistance()
    {
        var (_, sut) = MakeSut(maxDistance: 0.5f);

        var results = await sut.QueryAsync("proj1-kb", new float[] { 0.1f, 0.2f }, topK: 2);

        Assert.Single(results);
        Assert.Equal("doc-1", results[0].Id);
    }

    [Fact]
    public async Task QueryAsync_ReturnsEmptyList_WhenNoResultQualifiesUnderMaxDistance()
    {
        var (_, sut) = MakeSut(maxDistance: 0.05f);

        var results = await sut.QueryAsync("proj1-kb", new float[] { 0.1f, 0.2f }, topK: 2);

        Assert.Empty(results);
    }
}
