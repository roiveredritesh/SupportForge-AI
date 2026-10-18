using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Graph;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class CodeVectorIndexTests
{
    [Theory]
    [InlineData("django/core/signing.py::Signer", false, true)]
    [InlineData("pkg/engine.go::NewEngine", false, true)]
    [InlineData("django/core/signing.py::Signer.sign", false, false)] // a member: its class carries it
    [InlineData("engine.go::Engine.NewInstantQuery", false, false)]
    [InlineData("django/core/signing.py", false, false)] // a file node
    [InlineData("endpoint::GET api/orders", false, false)]
    [InlineData("tests/test_signing.py::SignerTests", true, false)]
    public void IsIndexable_OnlyTopLevelDefinitionsOutsideTests(string id, bool isTest, bool expected)
    {
        Assert.Equal(expected, CodeVectorIndex.IsIndexable(new CodeGraphNode { Id = id, IsTest = isTest }));
    }

    [Fact]
    public void Text_IsNameSummaryAndLeadingBodyTerms()
    {
        var node = new CodeGraphNode
        {
            Label = "Signer",
            Summary = "",
            BodyTerms = string.Join(' ', new[] { "signature", "sign", "unsign", "salt" }.Concat(Enumerable.Range(0, 100).Select(i => $"w{i}"))),
        };

        var text = CodeVectorIndex.Text(node);

        Assert.StartsWith("Signer: signature sign unsign salt", text);
        Assert.DoesNotContain("w99", text);
    }

    [Fact]
    public void MergeRanked_RaisesWhatBothFind_AndKeepsTheRest()
    {
        var merged = GraphDbQueryTool.MergeRanked(["values", "headers", "Signer"], ["Signer", "signing"], s => s);

        Assert.Equal("Signer", merged[0]);
        // headers (keyword rank 2) and signing (semantic rank 2) score the same; the keyword hit goes first.
        Assert.Equal(["Signer", "values", "headers", "signing"], merged);
    }

    [Fact]
    public void MergeRanked_WithNoSemanticHits_IsTheKeywordOrder()
    {
        Assert.Equal(["a", "b"], GraphDbQueryTool.MergeRanked(["a", "b"], [], s => s));
    }

    [Fact]
    public async Task Job_WithoutConsent_RemovesTheRepositoryVectorsAndEmbedsNothing()
    {
        var store = new Mock<IVectorStoreService>();
        var embeddings = new Mock<ILlmEmbeddingClient>(MockBehavior.Strict);
        var projects = new Mock<IProjectRepository>();
        projects.Setup(p => p.GetByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Project { Id = "p1", Name = "P", OrgId = "o1", CodeClassificationEnabled = false });
        var orgs = new Mock<IOrgRepository>();

        var job = new CodeVectorIndexJob("p1", "/nonexistent/graph.json", "repo", store.Object, embeddings.Object,
            projects.Object, orgs.Object, NullLogger.Instance);
        await job.RunAsync(CancellationToken.None);

        store.Verify(s => s.DeleteByMetadataAsync("p1-code",
            It.Is<IReadOnlyDictionary<string, string>>(d => d["repo"] == "repo"), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.UpsertAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Job_WithConsent_EmbedsTopLevelDefinitionsInBatches()
    {
        var graphPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}-graph.json");
        var graph = new CodeGraphFile
        {
            Nodes =
            [
                new CodeGraphNode { Id = "signing.py", Label = "signing.py" },
                new CodeGraphNode { Id = "signing.py::Signer", Label = "Signer", BodyTerms = "signature sign" },
                new CodeGraphNode { Id = "signing.py::Signer.sign", Label = "sign" },
            ],
        };
        await File.WriteAllTextAsync(graphPath, System.Text.Json.JsonSerializer.Serialize(graph));
        try
        {
            var store = new Mock<IVectorStoreService>();
            var embeddings = new FakeEmbeddings();
            var projects = new Mock<IProjectRepository>();
            projects.Setup(p => p.GetByIdAsync("p1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Project { Id = "p1", Name = "P", OrgId = "o1", CodeClassificationEnabled = true });
            var orgs = new Mock<IOrgRepository>();
            orgs.Setup(o => o.GetByIdAsync("o1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Org { Id = "o1", Name = "O", ContactPerson = "c", ContactNumber = "0", Industry = "i", CodeClassificationEnabled = true });

            var job = new CodeVectorIndexJob("p1", graphPath, "repo", store.Object, embeddings,
                projects.Object, orgs.Object, NullLogger.Instance);
            await job.RunAsync(CancellationToken.None);

            store.Verify(s => s.UpsertAsync("p1-code",
                It.Is<IReadOnlyList<VectorDocument>>(docs => docs.Count == 1
                    && docs[0].Id == "repo::signing.py::Signer"
                    && docs[0].Metadata["nodeId"] == "signing.py::Signer"
                    && docs[0].Text.StartsWith("Signer:")),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            File.Delete(graphPath);
        }
    }

    private sealed class FakeEmbeddings : ILlmEmbeddingClient
    {
        public int LastTotalTokens => 0;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default, EmbeddingPurpose purpose = EmbeddingPurpose.Query) =>
            Task.FromResult(new[] { 0.1f });

        public Task<IReadOnlyList<float[]>> EmbedManyAsync(
            IReadOnlyList<string> texts, CancellationToken ct = default, EmbeddingPurpose purpose = EmbeddingPurpose.Passage) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new[] { 0.1f }).ToList());
    }
}
