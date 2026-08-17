using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Graph;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class CodeNodeClassifierTests : IDisposable
{
    private readonly string _repoDir;
    private readonly string _hashDir;
    private readonly Mock<ILlmChatClient> _llm;
    private readonly IContentHashRepository _hashes;

    public CodeNodeClassifierTests()
    {
        _repoDir = Directory.CreateTempSubdirectory().FullName;
        _hashDir = Directory.CreateTempSubdirectory().FullName;
        _llm = new Mock<ILlmChatClient>();
        _hashes = new JsonFileContentHashRepository(_hashDir);
    }

    public void Dispose()
    {
        Directory.Delete(_repoDir, recursive: true);
        Directory.Delete(_hashDir, recursive: true);
    }

    private CodeNodeClassifier MakeClassifier(string modelId = "test-model") =>
        new(_llm.Object, _hashes, modelId, NullLogger<CodeNodeClassifier>.Instance);

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_repoDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private void SetLlmResponse(string json) =>
        _llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(json);

    [Fact]
    public async Task ClassifyAsync_AppliesObservations_ForCleanBatchResponse()
    {
        WriteFile("Auth.cs", "namespace Demo;\npublic class Auth { public void Login() {} }\n");
        var graph = CodeGraphExtractor.Extract(_repoDir);
        SetLlmResponse("""
            [{"id": "Auth.cs", "kind": "handwritten", "confidence": 0.9, "purpose": "Handles login.", "domainTerms": ["auth"], "layer": "service", "definitions": [{"name": "Auth", "purpose": "Auth service class."}]}]
            """);

        await MakeClassifier().ClassifyAsync(graph, _repoDir, "proj1", "repo1", CancellationToken.None);

        var fileNode = graph.Nodes.Single(n => n.Id == "Auth.cs");
        Assert.Equal("handwritten", fileNode.Kind);
        Assert.Equal(0.9, fileNode.Confidence);
        Assert.Equal("Handles login.", fileNode.Purpose);
        Assert.Contains("auth", fileNode.DomainTerms);
        Assert.Equal("service", fileNode.Layer);

        var defNode = graph.Nodes.Single(n => n.Id == "Auth.cs::Auth");
        Assert.Equal("handwritten", defNode.Kind);
        Assert.Equal(0.9, defNode.Confidence);
        Assert.Equal("service", defNode.Layer);
        Assert.Equal("Auth service class.", defNode.Purpose);
    }

    [Fact]
    public async Task ClassifyAsync_MissingIdInResponse_LeavesThatFileUnenriched_RestStillClassified()
    {
        WriteFile("A.cs", "namespace Demo;\npublic class A {}\n");
        WriteFile("B.cs", "namespace Demo;\npublic class B {}\n");
        var graph = CodeGraphExtractor.Extract(_repoDir);
        // Only B.cs present in the response -- A.cs is missing entirely.
        SetLlmResponse("""
            [{"id": "B.cs", "kind": "handwritten", "confidence": 0.8, "purpose": "B.", "domainTerms": [], "layer": "util", "definitions": []}]
            """);

        await MakeClassifier().ClassifyAsync(graph, _repoDir, "proj1", "repo1", CancellationToken.None);

        Assert.Equal("", graph.Nodes.Single(n => n.Id == "A.cs").Kind);
        Assert.Equal("handwritten", graph.Nodes.Single(n => n.Id == "B.cs").Kind);
    }

    [Fact]
    public async Task ClassifyAsync_UnrecognizedIdInResponse_IsDiscarded_DoesNotCorruptOtherFiles()
    {
        WriteFile("A.cs", "namespace Demo;\npublic class A {}\n");
        var graph = CodeGraphExtractor.Extract(_repoDir);
        SetLlmResponse("""
            [{"id": "A.cs", "kind": "handwritten", "confidence": 0.8, "purpose": "A.", "domainTerms": [], "layer": "util", "definitions": []},
             {"id": "NotSubmitted.cs", "kind": "vendored", "confidence": 0.99, "purpose": "x", "domainTerms": [], "layer": "util", "definitions": []}]
            """);

        await MakeClassifier().ClassifyAsync(graph, _repoDir, "proj1", "repo1", CancellationToken.None);

        Assert.Equal("handwritten", graph.Nodes.Single(n => n.Id == "A.cs").Kind);
        Assert.DoesNotContain(graph.Nodes, n => n.Id == "NotSubmitted.cs");
    }

    [Fact]
    public async Task ClassifyAsync_UnrecognizedKindValue_CoercedToUnknown()
    {
        WriteFile("A.cs", "namespace Demo;\npublic class A {}\n");
        var graph = CodeGraphExtractor.Extract(_repoDir);
        SetLlmResponse("""
            [{"id": "A.cs", "kind": "something-the-model-made-up", "confidence": 0.5, "purpose": "A.", "domainTerms": [], "layer": "not-a-real-layer", "definitions": []}]
            """);

        await MakeClassifier().ClassifyAsync(graph, _repoDir, "proj1", "repo1", CancellationToken.None);

        var node = graph.Nodes.Single(n => n.Id == "A.cs");
        Assert.Equal("unknown", node.Kind);
        Assert.Equal("unknown", node.Layer);
    }

    [Fact]
    public async Task ClassifyAsync_LlmThrows_LeavesFileUnenriched_NoExceptionPropagates()
    {
        WriteFile("A.cs", "namespace Demo;\npublic class A {}\n");
        var graph = CodeGraphExtractor.Extract(_repoDir);
        _llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await MakeClassifier().ClassifyAsync(graph, _repoDir, "proj1", "repo1", CancellationToken.None);

        Assert.Equal("", graph.Nodes.Single(n => n.Id == "A.cs").Kind);
    }

    // Bug fix: observed live against the real configured model -- despite the system prompt's
    // explicit "respond with ONLY a JSON array" instruction, some responses came back as multiple
    // separate top-level arrays ("[{...file1}]\n\n[{...file2}]") instead of one combined array. The
    // original substring-based parser treated that as one malformed blob and failed the whole batch.
    [Fact]
    public async Task ClassifyAsync_MultipleConcatenatedJsonArrays_ParsesAndMergesAll()
    {
        WriteFile("a.cs", "namespace Demo;\npublic class A {}\n");
        WriteFile("b.cs", "namespace Demo;\npublic class B {}\n");
        var graph = CodeGraphExtractor.Extract(_repoDir);
        SetLlmResponse("""
            [{"id": "a.cs", "kind": "handwritten", "confidence": 0.8, "purpose": "A.", "domainTerms": [], "layer": "util", "definitions": []}]

            [{"id": "b.cs", "kind": "test", "confidence": 0.9, "purpose": "B.", "domainTerms": [], "layer": "test", "definitions": []}]
            """);

        await MakeClassifier().ClassifyAsync(graph, _repoDir, "proj1", "repo1", CancellationToken.None);

        Assert.Equal("handwritten", graph.Nodes.Single(n => n.Id == "a.cs").Kind);
        Assert.Equal("test", graph.Nodes.Single(n => n.Id == "b.cs").Kind);
    }

    // A trailing array that never closed (real truncation) must not sink an earlier, complete array
    // in the same response -- only the file(s) in the broken segment stay unenriched.
    [Fact]
    public async Task ClassifyAsync_TruncatedTrailingArray_SalvagesEarlierCompleteArray()
    {
        WriteFile("a.cs", "namespace Demo;\npublic class A {}\n");
        WriteFile("b.cs", "namespace Demo;\npublic class B {}\n");
        var graph = CodeGraphExtractor.Extract(_repoDir);
        SetLlmResponse("""
            [{"id": "a.cs", "kind": "handwritten", "confidence": 0.8, "purpose": "A.", "domainTerms": [], "layer": "util", "definitions": []}]

            [{"id": "b.cs", "kind": "test", "confidence": 0.9, "purpose": "B truncated mid
            """);

        await MakeClassifier().ClassifyAsync(graph, _repoDir, "proj1", "repo1", CancellationToken.None);

        Assert.Equal("handwritten", graph.Nodes.Single(n => n.Id == "a.cs").Kind);
        Assert.Equal("", graph.Nodes.Single(n => n.Id == "b.cs").Kind);
    }

    // Bug fix: observed live -- the model repeated the same file's id across two of its separate
    // top-level arrays. Once ParseResponse merges items from multiple arrays, that duplicate id
    // must not crash batch processing; the first occurrence wins.
    [Fact]
    public async Task ClassifyAsync_DuplicateIdAcrossMergedArrays_DoesNotThrow_KeepsFirstOccurrence()
    {
        WriteFile("a.cs", "namespace Demo;\npublic class A {}\n");
        var graph = CodeGraphExtractor.Extract(_repoDir);
        SetLlmResponse("""
            [{"id": "a.cs", "kind": "handwritten", "confidence": 0.8, "purpose": "first", "domainTerms": [], "layer": "util", "definitions": []}]

            [{"id": "a.cs", "kind": "vendored", "confidence": 0.9, "purpose": "second", "domainTerms": [], "layer": "util", "definitions": []}]
            """);

        await MakeClassifier().ClassifyAsync(graph, _repoDir, "proj1", "repo1", CancellationToken.None);

        Assert.Equal("handwritten", graph.Nodes.Single(n => n.Id == "a.cs").Kind);
        Assert.Equal("first", graph.Nodes.Single(n => n.Id == "a.cs").Purpose);
    }

    [Fact]
    public async Task ClassifyAsync_MalformedResponse_LeavesFileUnenriched_NoExceptionPropagates()
    {
        WriteFile("A.cs", "namespace Demo;\npublic class A {}\n");
        var graph = CodeGraphExtractor.Extract(_repoDir);
        SetLlmResponse("this is not json at all");

        await MakeClassifier().ClassifyAsync(graph, _repoDir, "proj1", "repo1", CancellationToken.None);

        Assert.Equal("", graph.Nodes.Single(n => n.Id == "A.cs").Kind);
    }

    [Fact]
    public async Task ClassifyAsync_MinifiedFile_TruncatesPromptInputTo2KB()
    {
        var longLine = new string('a', 5000);
        WriteFile("moment.js", "/*! moment.js v2.29.4 | Copyright (c) 2024 Moment.js */\nfunction enable(){" + longLine + "}\n");
        var graph = CodeGraphExtractor.Extract(_repoDir);
        Assert.Contains("Minified", graph.Nodes.Single(n => n.Id == "moment.js").Shape);

        string? capturedPrompt = null;
        _llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, user, _) => capturedPrompt = user)
            .ReturnsAsync("""[{"id": "moment.js", "kind": "vendored", "confidence": 0.9, "purpose": "x", "domainTerms": [], "layer": "util", "definitions": []}]""");

        await MakeClassifier().ClassifyAsync(graph, _repoDir, "proj1", "repo1", CancellationToken.None);

        Assert.NotNull(capturedPrompt);
        // Full content is ~5060 chars; capped input must be far shorter than that.
        Assert.True(capturedPrompt!.Length < 3000, $"Expected prompt to stay near the 2KB cap, was {capturedPrompt.Length} chars");
    }

    [Fact]
    public async Task ClassifyAsync_UnchangedFile_SecondRunMakesNoLlmCall_ReusesCachedObservation()
    {
        WriteFile("A.cs", "namespace Demo;\npublic class A {}\n");
        SetLlmResponse("""[{"id": "A.cs", "kind": "handwritten", "confidence": 0.7, "purpose": "A.", "domainTerms": [], "layer": "util", "definitions": []}]""");

        var graph1 = CodeGraphExtractor.Extract(_repoDir);
        await MakeClassifier().ClassifyAsync(graph1, _repoDir, "proj1", "repo1", CancellationToken.None);
        _llm.Invocations.Clear();

        var graph2 = CodeGraphExtractor.Extract(_repoDir);
        await MakeClassifier().ClassifyAsync(graph2, _repoDir, "proj1", "repo1", CancellationToken.None);

        _llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("handwritten", graph2.Nodes.Single(n => n.Id == "A.cs").Kind);
        Assert.Equal("A.", graph2.Nodes.Single(n => n.Id == "A.cs").Purpose);
    }

    [Fact]
    public async Task ClassifyAsync_FileContentChanged_ReclassifiesInstead_OfUsingStaleCache()
    {
        WriteFile("A.cs", "namespace Demo;\npublic class A {}\n");
        SetLlmResponse("""[{"id": "A.cs", "kind": "handwritten", "confidence": 0.7, "purpose": "first", "domainTerms": [], "layer": "util", "definitions": []}]""");
        var graph1 = CodeGraphExtractor.Extract(_repoDir);
        await MakeClassifier().ClassifyAsync(graph1, _repoDir, "proj1", "repo1", CancellationToken.None);

        WriteFile("A.cs", "namespace Demo;\npublic class A { public void Changed() {} }\n");
        SetLlmResponse("""[{"id": "A.cs", "kind": "handwritten", "confidence": 0.7, "purpose": "second", "domainTerms": [], "layer": "util", "definitions": []}]""");
        var graph2 = CodeGraphExtractor.Extract(_repoDir);
        await MakeClassifier().ClassifyAsync(graph2, _repoDir, "proj1", "repo1", CancellationToken.None);

        Assert.Equal("second", graph2.Nodes.Single(n => n.Id == "A.cs").Purpose);
        _llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ClassifyAsync_ModelIdChanged_InvalidatesCache_Reclassifies()
    {
        WriteFile("A.cs", "namespace Demo;\npublic class A {}\n");
        SetLlmResponse("""[{"id": "A.cs", "kind": "handwritten", "confidence": 0.7, "purpose": "x", "domainTerms": [], "layer": "util", "definitions": []}]""");
        var graph1 = CodeGraphExtractor.Extract(_repoDir);
        await MakeClassifier(modelId: "model-a").ClassifyAsync(graph1, _repoDir, "proj1", "repo1", CancellationToken.None);
        _llm.Invocations.Clear();

        var graph2 = CodeGraphExtractor.Extract(_repoDir);
        await MakeClassifier(modelId: "model-b").ClassifyAsync(graph2, _repoDir, "proj1", "repo1", CancellationToken.None);

        _llm.Verify(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ClassifyAsync_FailedClassification_IsNeverCached_RetriesNextRun()
    {
        WriteFile("A.cs", "namespace Demo;\npublic class A {}\n");
        _llm.Setup(l => l.CompleteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var graph1 = CodeGraphExtractor.Extract(_repoDir);
        await MakeClassifier().ClassifyAsync(graph1, _repoDir, "proj1", "repo1", CancellationToken.None);

        SetLlmResponse("""[{"id": "A.cs", "kind": "handwritten", "confidence": 0.7, "purpose": "recovered", "domainTerms": [], "layer": "util", "definitions": []}]""");
        var graph2 = CodeGraphExtractor.Extract(_repoDir);
        await MakeClassifier().ClassifyAsync(graph2, _repoDir, "proj1", "repo1", CancellationToken.None);

        Assert.Equal("recovered", graph2.Nodes.Single(n => n.Id == "A.cs").Purpose);
    }

    [Fact]
    public async Task ClassifyAsync_SamePathInDifferentProjects_IndependentCacheEntries()
    {
        WriteFile("A.cs", "namespace Demo;\npublic class A {}\n");
        SetLlmResponse("""[{"id": "A.cs", "kind": "handwritten", "confidence": 0.7, "purpose": "proj1-value", "domainTerms": [], "layer": "util", "definitions": []}]""");
        var graph1 = CodeGraphExtractor.Extract(_repoDir);
        await MakeClassifier().ClassifyAsync(graph1, _repoDir, "proj1", "repo1", CancellationToken.None);

        SetLlmResponse("""[{"id": "A.cs", "kind": "handwritten", "confidence": 0.7, "purpose": "proj2-value", "domainTerms": [], "layer": "util", "definitions": []}]""");
        var graph2 = CodeGraphExtractor.Extract(_repoDir);
        await MakeClassifier().ClassifyAsync(graph2, _repoDir, "proj2", "repo1", CancellationToken.None);

        Assert.Equal("proj1-value", graph1.Nodes.Single(n => n.Id == "A.cs").Purpose);
        Assert.Equal("proj2-value", graph2.Nodes.Single(n => n.Id == "A.cs").Purpose);
    }
}
