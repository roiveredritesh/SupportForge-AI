using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Ingestion.Code;
using SupportForge.Ingestion.Graph;

// Phase A1 eval harness (docs/architecture/2026-08-06-003-gap-closing-solutions.md): replays a
// domain-diverse, project-agnostic fixture set through the *real* verifier classes (not a
// reimplementation of their prompts) against the *real* configured LLM, and reports agreement rate.
// Offline/on-demand tool -- never wired into CoordinatorPipeline or any runtime path.

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Development.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddLlmProviders(configuration); // same DI wiring SupportForge.Api uses -- one provider, config-selected
using var provider = services.BuildServiceProvider();

var llm = provider.GetRequiredService<ILlmChatClient>();
var loggers = provider.GetRequiredService<ILoggerFactory>();

var fixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
var results = new List<CaseResult>();

results.AddRange(await RunKbFixturesAsync());
results.AddRange(await RunCodeFixturesAsync());
results.AddRange(await RunVisionFixturesAsync());
results.AddRange(await RunClassificationFixturesAsync());
results.AddRange(await RunRetrievalEvalAsync());

Report(results);
return results.Any(r => !r.Agreed) ? 1 : 0;

async Task<List<CaseResult>> RunKbFixturesAsync()
{
    var fixtures = LoadFixtures<KbFixture>("kb-fixtures.json");
    var outcomes = new List<CaseResult>();
    foreach (var f in fixtures)
    {
        var context = new AgentContext { ProjectId = "eval", Query = f.Query, Intent = "kb_question" };
        foreach (var s in f.Snippets) context.KbSnippets.Add(s);

        var verifier = new KbResearcherVerifier(llm, loggers.CreateLogger<KbResearcherVerifier>());
        await verifier.RunAsync(context);

        var actualPass = context.KbVerification.Status == VerificationStatus.Passed;
        outcomes.Add(new CaseResult("KbResearcherVerifier", f.Id, f.Domain, f.Query, f.ExpectPass, actualPass, context.KbVerification.Reason));
    }
    return outcomes;
}

async Task<List<CaseResult>> RunCodeFixturesAsync()
{
    var fixtures = LoadFixtures<CodeFixture>("code-fixtures.json");
    var outcomes = new List<CaseResult>();
    foreach (var f in fixtures)
    {
        var context = new AgentContext { ProjectId = "eval", Query = f.Query, Intent = "code_question" };
        foreach (var s in f.Snippets) context.CodeSnippets.Add(s);

        var verifier = new CodeAnalyzerVerifier(llm, loggers.CreateLogger<CodeAnalyzerVerifier>());
        await verifier.RunAsync(context);

        var actualPass = context.CodeVerification.Status == VerificationStatus.Passed;
        outcomes.Add(new CaseResult("CodeAnalyzerVerifier", f.Id, f.Domain, f.Query, f.ExpectPass, actualPass, context.CodeVerification.Reason));
    }
    return outcomes;
}

async Task<List<CaseResult>> RunVisionFixturesAsync()
{
    var fixtures = LoadFixtures<VisionFixture>("vision-fixtures.json");
    var outcomes = new List<CaseResult>();
    foreach (var f in fixtures)
    {
        var context = new AgentContext { ProjectId = "eval", Query = f.Query, ScreenshotBase64 = "dummy" };
        context.VisionFindings = f.Findings;

        var verifier = new VisionAnalyzerVerifier(llm, loggers.CreateLogger<VisionAnalyzerVerifier>());
        await verifier.RunAsync(context);

        var actualPass = context.VisionVerification.Status == VerificationStatus.Passed;
        var judged = !string.IsNullOrWhiteSpace(f.Findings) && f.Findings.Length < 20;
        var domain = judged ? "LLM-judged" : "short-circuit (no LLM call)";
        outcomes.Add(new CaseResult("VisionAnalyzerVerifier", f.Id, domain, f.Query, f.ExpectPass, actualPass, context.VisionVerification.Reason));
    }
    return outcomes;
}

// U11 (docs/plans/2026-08-16-001-feat-code-graph-classification-plan.md): classification accuracy is
// a proxy, not the goal -- seeded from the actual incident (moment.js/rater-js vendor samples) plus
// the two failure directions of path-based exclusion (E2: a business file physically inside a
// "vendor/" folder). Real customer source (AajLogistics-Dhar) is never committed here; these are
// synthetic files with the same shape (banner+minified for vendor, domain-named symbols for business
// logic) rather than the actual proprietary content.
async Task<List<CaseResult>> RunClassificationFixturesAsync()
{
    var fixtures = LoadFixtures<ClassificationFixture>("classification-fixtures.json");
    if (fixtures.Count == 0) return [];

    var graph = await BuildAndClassifyEvalGraphAsync(fixtures);
    var outcomes = new List<CaseResult>();
    foreach (var f in fixtures)
    {
        var node = graph.Nodes.FirstOrDefault(n => n.Id == f.FileName);
        var actualKind = node?.Kind ?? "(no node found)";
        var actualPass = actualKind == f.ExpectKind;
        outcomes.Add(new CaseResult("CodeNodeClassifier", f.Id, f.Domain, f.FileName, true, actualPass, $"expected kind={f.ExpectKind} actual kind={actualKind}"));
    }
    return outcomes;
}

// The classification accuracy check above is a proxy -- this is what actually proves the original
// incident is fixed: the same fixture set, imported into a scratch Neo4j project, then queried with
// a question shaped like the incident's own failing query. Skips gracefully (empty result, doesn't
// fail the eval run) when Neo4j isn't configured or isn't reachable -- this offline tool has no other
// dependency on Neo4j today, so a dev running it without a local instance shouldn't get a hard failure.
async Task<List<CaseResult>> RunRetrievalEvalAsync()
{
    var neo4jSection = configuration.GetSection("Neo4j");
    if (!neo4jSection.Exists()) return [];

    var neo4jOptions = neo4jSection.Get<Neo4jOptions>() ?? new Neo4jOptions();
    var authToken = string.IsNullOrEmpty(neo4jOptions.Password)
        ? AuthTokens.None
        : AuthTokens.Basic(neo4jOptions.User, neo4jOptions.Password);
    await using var driver = GraphDatabase.Driver(neo4jOptions.Uri, authToken);

    try
    {
        await driver.VerifyConnectivityAsync();
    }
    catch (Exception)
    {
        return []; // Neo4j not reachable -- skip gracefully rather than failing the whole eval run
    }

    const string evalProjectId = "eval-classification-retrieval";
    const string evalRepo = "eval-repo";
    var outcomes = new List<CaseResult>();

    try
    {
        var fixtures = LoadFixtures<ClassificationFixture>("classification-fixtures.json");
        if (fixtures.Count == 0) return [];

        var graph = await BuildAndClassifyEvalGraphAsync(fixtures);
        var graphJsonPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}-graph.json");
        await File.WriteAllTextAsync(graphJsonPath, JsonSerializer.Serialize(graph));

        try
        {
            var importJob = new GraphImportJob(evalProjectId, graphJsonPath, evalRepo, driver, neo4jOptions.Database);
            await importJob.RunAsync(CancellationToken.None);

            var queryTool = new GraphDbQueryTool(driver, neo4jOptions.Database);
            // Shaped like the incident's own reported failing query -- the actual proof this design
            // exists to deliver is that this now surfaces the business fixture, not the vendor ones.
            const string question = "How do I register a new vehicle for a driver?";
            var context = await queryTool.QueryAsync(evalProjectId, question, retrying: false, CancellationToken.None);

            var citedVendor = context is not null && (context.Contains("moment.js") || context.Contains("rater-js"));
            var citedBusiness = context is not null && context.Contains("VehicleService");
            var passed = citedBusiness && !citedVendor;
            outcomes.Add(new CaseResult(
                "RetrievalEval", "vehicle-registration-question", "retrieval", question, true, passed,
                $"citedBusiness={citedBusiness} citedVendor={citedVendor} context={Truncate(context, 300)}"));
        }
        finally
        {
            File.Delete(graphJsonPath);
        }
    }
    finally
    {
        // Scratch project cleanup -- this eval's own data must not linger in a shared Neo4j instance.
        await using var session = driver.AsyncSession(o => o.WithDatabase(neo4jOptions.Database));
        await session.ExecuteWriteAsync(tx => tx.RunAsync(
            "MATCH (n:GraphNode {projectId: $projectId}) DETACH DELETE n", new { projectId = evalProjectId }));
    }

    return outcomes;
}

async Task<CodeGraphFile> BuildAndClassifyEvalGraphAsync(List<ClassificationFixture> fixtures)
{
    var repoDir = Directory.CreateTempSubdirectory().FullName;
    var hashDir = Directory.CreateTempSubdirectory().FullName;
    try
    {
        foreach (var f in fixtures)
        {
            var fullPath = Path.Combine(repoDir, f.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, f.Content);
        }

        var graph = CodeGraphExtractor.Extract(repoDir);
        var hashes = new JsonFileContentHashRepository(hashDir);
        var llmProvider = configuration["Llm:Provider"];
        llmProvider = string.IsNullOrWhiteSpace(llmProvider) ? "OpenAI" : llmProvider;
        var modelId = configuration[$"Llm:{llmProvider}:ChatModel"] ?? "unknown";
        var classifier = new CodeNodeClassifier(llm, hashes, modelId, loggers.CreateLogger<CodeNodeClassifier>());
        await classifier.ClassifyAsync(graph, repoDir, "eval", "eval-repo", CancellationToken.None);
        return graph;
    }
    finally
    {
        Directory.Delete(repoDir, recursive: true);
        Directory.Delete(hashDir, recursive: true);
    }
}

static string Truncate(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];

List<T> LoadFixtures<T>(string fileName)
{
    var path = Path.Combine(fixturesDir, fileName);
    var json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<List<T>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException($"{fileName} deserialized to null.");
}

void Report(List<CaseResult> allResults)
{
    Console.WriteLine();
    Console.WriteLine($"=== Eval results ({DateTime.UtcNow:u}) ===");
    foreach (var group in allResults.GroupBy(r => r.Verifier))
    {
        var total = group.Count();
        var agreed = group.Count(r => r.Agreed);
        Console.WriteLine($"\n{group.Key}: {agreed}/{total} agreed ({100.0 * agreed / total:F0}%)");
        foreach (var r in group.Where(r => !r.Agreed))
            Console.WriteLine($"  DISAGREE [{r.Id}] domain={r.Domain} query=\"{r.Query}\" expected={r.Expected} actual={r.Actual} reason={r.Reason}");
    }

    var overallTotal = allResults.Count;
    var overallAgreed = allResults.Count(r => r.Agreed);
    Console.WriteLine($"\nOverall: {overallAgreed}/{overallTotal} agreed ({100.0 * overallAgreed / overallTotal:F0}%)");
}

sealed record CaseResult(string Verifier, string Id, string Domain, string Query, bool Expected, bool Actual, string? Reason)
{
    public bool Agreed => Expected == Actual;
}

sealed class KbFixture
{
    public string Id { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Query { get; set; } = "";
    public List<string> Snippets { get; set; } = new();
    public bool ExpectPass { get; set; }
}

sealed class CodeFixture
{
    public string Id { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Query { get; set; } = "";
    public List<string> Snippets { get; set; } = new();
    public bool ExpectPass { get; set; }
}

sealed class ClassificationFixture
{
    public string Id { get; set; } = "";
    public string Domain { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Content { get; set; } = "";
    public string ExpectKind { get; set; } = "";
}

sealed class VisionFixture
{
    public string Id { get; set; } = "";
    public string Query { get; set; } = "";
    public string Findings { get; set; } = "";
    public bool ExpectPass { get; set; }
}
