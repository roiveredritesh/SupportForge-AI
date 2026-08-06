using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SupportForge.Agents;

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

sealed class VisionFixture
{
    public string Id { get; set; } = "";
    public string Query { get; set; } = "";
    public string Findings { get; set; } = "";
    public bool ExpectPass { get; set; }
}
