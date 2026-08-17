using System.Net.Http.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SupportForge.Agents;
using SupportForge.Agents.Tools;
using SupportForge.Api.Contracts;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Documents;
using SupportForge.VectorStore.Chroma;
using Xunit;

namespace SupportForge.Api.Tests.Integration;

/// <summary>
/// RAG pipeline reliability plan (docs/plans/2026-08-13-001-feat-rag-pipeline-reliability-plan.md) --
/// end-to-end verification through the real ingestion classes into a REAL, locally running Chroma
/// instance (started via the `chroma run` CLI, no Docker). Proves U1/U2/U5/U7/U10 actually compose
/// correctly against a real vector store, not just in isolated unit tests with mocked collaborators.
///
/// Deliberately does NOT exercise the chat/judge/drafter path (Triage -> KbResearcherVerifier ->
/// DrafterAgent): that requires a live, quality LLM judgment call the eval harness
/// (SupportForge.Evals) already owns, and no live LLM API key is available in this environment.
/// <see cref="FakeEmbeddingClient"/> replaces only the embedding step so ingestion runs with no
/// external API dependency or cost.
/// </summary>
[Trait("Category", "Integration")]
public class RagPipelineE2ETests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ChromaBaseUrl = "http://localhost:8000";

    private readonly WebApplicationFactory<Program> _factory;

    public RagPipelineE2ETests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Deterministic, no-API-key embedding so ingestion exercises the real chunk -> embed
                // -> upsert path without needing a live LLM provider. Last registration wins for
                // GetRequiredService<T>(), so this replaces whichever real provider appsettings.json
                // configured (default: NvidiaNim).
                services.AddSingleton<ILlmEmbeddingClient, FakeEmbeddingClient>();
            });
        });
    }

    private async Task<bool> ChromaIsReachableAsync()
    {
        try
        {
            using var http = new HttpClient();
            var response = await http.GetAsync($"{ChromaBaseUrl}/api/v2/heartbeat");
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    [SkippableFact]
    public async Task DocumentSource_WithMarkdownDocxAndPdf_IngestsThroughRealApi_IntoRealChroma_WithTablesIntact()
    {
        Skip.IfNot(await ChromaIsReachableAsync(), $"requires a real Chroma instance at {ChromaBaseUrl} (start with `chroma run --path <dir> --port 8000`)");

        var projectId = "e2e-rag-" + Guid.NewGuid().ToString("N")[..8];
        var kbFolder = Path.Combine(Path.GetTempPath(), "supportforge-e2e-kb-" + Guid.NewGuid());
        Directory.CreateDirectory(kbFolder);
        try
        {
            // A Markdown table long enough that pre-U5 word-based chunking would have sliced it
            // mid-row at the 1000-char boundary; U5 must keep it atomic in one chunk.
            var mdTable = string.Join("\n", new[]
            {
                "| Plan | Price/mo | API calls/mo |",
                "| --- | --- | --- |",
                "| Starter | $0 | 1,000 |",
                "| Pro | $49 | 50,000 |",
                "| Enterprise | Custom | Unlimited |",
            });
            var padding = string.Join(" ", Enumerable.Repeat("filler", 140)); // pushes the table near/past the 1000-char boundary
            await File.WriteAllTextAsync(Path.Combine(kbFolder, "pricing.md"), $"# Pricing\n\n{padding}\n\n{mdTable}\n\n{padding}\n");

            await File.WriteAllBytesAsync(Path.Combine(kbFolder, "policy.docx"), BuildSampleDocxWithTable());

            var client = _factory.CreateClient();
            using (var scope = _factory.Services.CreateScope())
                await SeedTestUserAsync(scope.ServiceProvider);
            var token = await AuthenticateAsync(client);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var project = new Project
            {
                Id = projectId,
                Name = "E2E RAG Project",
                KbSources = new List<KbSourceConfig> { new(KbSourceType.Documents, kbFolder, null) },
            };
            (await client.PostAsJsonAsync("/api/projects", project)).EnsureSuccessStatusCode();
            (await client.PostAsJsonAsync("/api/ingestion/trigger", new { ProjectId = projectId })).EnsureSuccessStatusCode();

            using var queryScope = _factory.Services.CreateScope();
            var searchTool = queryScope.ServiceProvider.GetRequiredService<KbSearchTool>();
            var results = await WaitForIndexedContentAsync(searchTool, projectId, "pricing and policy", expectedMinCount: 2);

            var joined = string.Join("\n---\n", results.Select(r => r.Text));
            // U5: the Markdown table's header and every data row survive together in one chunk.
            Assert.Contains("| Plan | Price/mo | API calls/mo |", joined);
            Assert.Contains("| Pro | $49 | 50,000 |", joined);
            // U10: the .docx table converted to Markdown, not lost.
            Assert.Contains("Refunds", joined);
            Assert.Contains("| Window | Eligible |", joined);
            Assert.Contains("| 30 days | Yes |", joined);
        }
        finally
        {
            Directory.Delete(kbFolder, recursive: true);
        }
    }

    [SkippableFact]
    public async Task HtmlToMarkdownConverter_Captioner_KbVectorIndexer_RoundTripThroughRealChroma()
    {
        Skip.IfNot(await ChromaIsReachableAsync(), $"requires a real Chroma instance at {ChromaBaseUrl} (start with `chroma run --path <dir> --port 8000`)");

        // Confluence storage-format XHTML: one table (the failure the review catalogued as #1 --
        // <td>Plan</td><td>Price</td> collapsing to "PlanPrice") plus one drawio diagram macro
        // (failure #2 -- silently dropped).
        var html = """
            <p>Refund policy overview.</p>
            <table>
              <tr><th>Plan</th><th>Price</th></tr>
              <tr><td>Pro</td><td>$49</td></tr>
            </table>
            <ac:structured-macro ac:name="drawio"><ac:parameter ac:name="diagramName">refund-flow</ac:parameter></ac:structured-macro>
            """;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var converted = HtmlToMarkdownConverter.Convert(doc);

        using var scope = _factory.Services.CreateScope();
        var llm = scope.ServiceProvider.GetRequiredService<ILlmChatClient>();
        var captioner = new IngestionImageCaptioner(llm);

        var markdown = converted.Markdown;
        foreach (var token in converted.Images)
        {
            var caption = await captioner.CaptionAsync(
                token,
                _ => throw new InvalidOperationException("no real attachment store in this test -- exercises the placeholder fallback"),
                CancellationToken.None);
            markdown = markdown.Replace($"{{{{IMAGE:{(token.Kind == ImageSourceKind.Attachment ? "attachment" : "external")}:{token.SourceRef}|{token.AltOrName}}}}}", caption);
        }

        var projectId = "e2e-htmlconv-" + Guid.NewGuid().ToString("N")[..8];
        var indexer = scope.ServiceProvider.GetRequiredService<KbVectorIndexer>();
        await indexer.IndexAsync(projectId, [("confluence-page-1", markdown, "Refund Policy")], CancellationToken.None);

        var searchTool = scope.ServiceProvider.GetRequiredService<KbSearchTool>();
        var results = await searchTool.SearchAsync(projectId, "refund policy", topK: 5);
        var joined = string.Join("\n---\n", results.Select(r => r.Text));

        // U1/R1: table converted, cells distinct -- not "ProPrice"-style concatenation.
        Assert.Contains("| Plan | Price |", joined);
        Assert.Contains("| Pro | $49 |", joined);
        // U1/U2/R3: diagram macro produced a caption placeholder (no real vision LLM available in
        // this environment, so this exercises the "unsupported vision -> placeholder" fallback,
        // not a real caption) instead of the macro being silently dropped.
        Assert.Contains("[diagram:", joined);
    }

    [SkippableFact]
    public async Task ChromaMaxDistance_FiltersOutLowRelevanceMatches_AgainstRealChroma()
    {
        Skip.IfNot(await ChromaIsReachableAsync(), $"requires a real Chroma instance at {ChromaBaseUrl} (start with `chroma run --path <dir> --port 8000`)");

        using var scope = _factory.Services.CreateScope();
        var indexer = scope.ServiceProvider.GetRequiredService<KbVectorIndexer>();
        var projectId = "e2e-threshold-" + Guid.NewGuid().ToString("N")[..8];
        await indexer.IndexAsync(
            projectId,
            [
                ("on-topic", "Waza's refund API returns a 409 if the order was already refunded.", null),
                ("off-topic", "The quarterly board meeting is scheduled for the third Tuesday of next month.", null),
            ],
            CancellationToken.None);

        var embedClient = scope.ServiceProvider.GetRequiredService<ILlmEmbeddingClient>();
        var queryEmbedding = await embedClient.EmbedAsync("why does the refund API return 409", CancellationToken.None);

        // ChromaVectorStoreService captures MaxDistance from IOptions.Value once at construction
        // (correct for production, where config is set once at startup) -- mutating a resolved
        // IOptions<ChromaOptions>.Value at runtime would not retroactively affect the DI-registered
        // singleton, so build two throwaway instances directly with the value baked in instead.
        // ChromaOptions.BaseUrl defaults to http://localhost:8000, matching ChromaBaseUrl above.
        var unfiltered = new ChromaVectorStoreService(new HttpClient(), Microsoft.Extensions.Options.Options.Create(new ChromaOptions()));
        var allResults = await unfiltered.QueryAsync($"{projectId}-kb", queryEmbedding, topK: 5);
        Assert.Equal(2, allResults.Count); // baseline: no threshold set, both come back regardless of relevance

        // FakeEmbeddingClient is deterministic, normalized bag-of-words. Measured L2 distance from
        // the query to the on-topic snippet (shares "refund"/"api"/"409") is ~1.055; to the
        // off-topic snippet (no shared vocabulary) is ~1.175. 1.1 sits between the two.
        var filteredService = new ChromaVectorStoreService(new HttpClient(), Microsoft.Extensions.Options.Options.Create(new ChromaOptions { MaxDistance = 1.1f }));
        var filtered = await filteredService.QueryAsync($"{projectId}-kb", queryEmbedding, topK: 5);
        Assert.True(filtered.Count < allResults.Count, "MaxDistance should have filtered out at least the off-topic match");
        Assert.Contains(filtered, r => r.Text.Contains("refund API", StringComparison.OrdinalIgnoreCase));
    }

    // Background ingestion (queue -> IngestionBackgroundService -> DocumentIngestionJob) is
    // asynchronous relative to the trigger call, so poll via the same real KbSearchTool/Chroma
    // query path production code uses, rather than a hand-rolled Chroma REST call.
    private static async Task<IReadOnlyList<(string Text, string Source)>> WaitForIndexedContentAsync(
        KbSearchTool searchTool, string projectId, string query, int expectedMinCount, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var results = await searchTool.SearchAsync(projectId, query, topK: 10);
            if (results.Count >= expectedMinCount) return results;
            await Task.Delay(500);
        }
        throw new TimeoutException($"Ingestion for '{projectId}' did not produce at least {expectedMinCount} indexed chunks within {timeoutSeconds}s.");
    }

    private static async Task SeedTestUserAsync(IServiceProvider services)
    {
        var userManager = services.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();
        if (await userManager.FindByNameAsync("e2e-rag-tester") is null)
            await userManager.CreateAsync(new AppUser { Id = "e2e-rag-tester", UserName = "e2e-rag-tester" }, "Test-Password-123!");
    }

    private static async Task<string> AuthenticateAsync(HttpClient client)
    {
        var tokenResponse = await client.PostAsJsonAsync("/api/auth/token", new { UserName = "e2e-rag-tester", Password = "Test-Password-123!" });
        tokenResponse.EnsureSuccessStatusCode();
        return (await tokenResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static byte[] BuildSampleDocxWithTable()
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new Document();
            var body = mainPart.Document.AppendChild(new Body());
            body.AppendChild(new Paragraph(new Run(new Text("Refunds are processed according to the eligibility window below."))));

            var table = new Table();
            table.AppendChild(new TableProperties(new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));

            TableRow MakeRow(string a, string b) => new(
                new TableCell(new Paragraph(new Run(new Text(a)))),
                new TableCell(new Paragraph(new Run(new Text(b)))));

            table.AppendChild(MakeRow("Window", "Eligible"));
            table.AppendChild(MakeRow("30 days", "Yes"));
            table.AppendChild(MakeRow("90 days", "No"));
            body.AppendChild(table);
            mainPart.Document.Save();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// Deterministic, cost-free stand-in for a real embedding model: a small bag-of-words hash
    /// vector, normalized. Not semantically meaningful the way a real model's embedding is, but
    /// deterministic per input and close enough on shared vocabulary to give KNN/distance-threshold
    /// assertions a real (if crude) signal to work with -- sufficient for proving the ingest -> real
    /// Chroma -> retrieve wiring is correct, which is what these tests exist to check.
    /// </summary>
    private sealed class FakeEmbeddingClient : ILlmEmbeddingClient
    {
        private const int Dimensions = 32;
        public int LastTotalTokens { get; private set; }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default, EmbeddingPurpose purpose = EmbeddingPurpose.Query)
        {
            var vector = new float[Dimensions];
            var words = text.ToLowerInvariant().Split(
                [' ', '\t', '\n', '\r', '|', '-', '#', ',', '.', ':'],
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                var bucket = Math.Abs(StableHash(word)) % Dimensions;
                vector[bucket] += 1f;
            }
            var magnitude = MathF.Sqrt(vector.Sum(v => v * v));
            if (magnitude > 0)
                for (var i = 0; i < vector.Length; i++)
                    vector[i] /= magnitude;

            LastTotalTokens = words.Length;
            return Task.FromResult(vector);
        }

        // Bug fix: string.GetHashCode() is randomized per process in .NET (a hash-flooding
        // mitigation), so the word-to-bucket assignment above -- and therefore the resulting
        // vectors' relative distances -- silently changed on every test run despite this class's
        // own doc comment claiming "deterministic". ChromaMaxDistance_FiltersOutLowRelevanceMatches_
        // AgainstRealChroma hardcodes a threshold (1.1) between two specific measured distances
        // (~1.055 on-topic, ~1.175 off-topic); a shifted bucket assignment could change which side
        // of that threshold either distance landed on, making the test intermittently fail. FNV-1a
        // is a fixed, well-known 32-bit hash with no per-process seed, so the same word always maps
        // to the same bucket across every run.
        private static int StableHash(string s)
        {
            unchecked
            {
                const uint fnvOffsetBasis = 2166136261;
                const uint fnvPrime = 16777619;
                var hash = fnvOffsetBasis;
                foreach (var c in s)
                {
                    hash ^= c;
                    hash *= fnvPrime;
                }
                return (int)hash;
            }
        }
    }
}
