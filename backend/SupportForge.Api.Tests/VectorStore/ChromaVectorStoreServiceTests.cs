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
    [Fact]
    public async Task QueryAsync_ParsesChromaResponse_IntoVectorQueryResults()
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
                  "ids": [["doc-1"]],
                  "documents": [["hello world"]],
                  "distances": [[0.12]],
                  "metadatas": [[{"source": "kb"}]]
                }
                """)
            });

        var client = new HttpClient(handler.Object) { BaseAddress = new Uri("http://localhost:8000") };
        var options = Options.Create(new ChromaOptions { BaseUrl = "http://localhost:8000" });
        var sut = new ChromaVectorStoreService(client, options);

        var results = await sut.QueryAsync("proj1-kb", new float[] { 0.1f, 0.2f }, topK: 1);

        Assert.Single(results);
        Assert.Equal("doc-1", results[0].Id);
        Assert.Equal("hello world", results[0].Text);
        Assert.Equal("kb", results[0].Metadata["source"]);
    }
}
