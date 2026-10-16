using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using SupportForge.Agents;
using SupportForge.Agents.Guard;
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

// Guard results are reported only and never affect the exit code: the heuristic layer is flag-only by design.
var guardResults = await RunGuardFixturesAsync("heuristic", null);
ReportGuard(guardResults);
// A Jev-backed IInputGuard gets a second call here once the Jev client exists:
// ReportGuard(await RunGuardFixturesAsync("jev", jevGuard), jevSupplied: true);

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

// U15 guard eval (docs/plans/2026-10-16-002-feat-jev-intent-classification-plan.md). Each fixture runs through the
// real HeuristicInputGuard and GuardPolicy. Options bind from the "Guard" section; Mode is then forced to Enforce so
// the decision logic is always exercised, whatever the configured rollout mode is.
async Task<List<GuardCaseResult>> RunGuardFixturesAsync(string guardName, IInputGuard? jevGuard)
{
    // GuardOptions is a plain class (not a record), so Mode is set on the freshly bound instance.
    var options = configuration.GetSection("Guard").Get<GuardOptions>() ?? new GuardOptions();
    options.Mode = GuardMode.Enforce;

    var heuristic = new HeuristicInputGuard(options);
    var fixtures = LoadFixtures<GuardFixture>("guard-fixtures.json");
    var outcomes = new List<GuardCaseResult>();
    foreach (var f in fixtures)
    {
        var input = new GuardInput(
            f.Message,
            f.History.Select(h => (Role: h.Role, Content: h.Content)).ToList(),
            f.HasScreenshot);

        var heuristicAssessment = await heuristic.AssessAsync(input, CancellationToken.None);

        GuardAssessment? jevAssessment = null;
        var jevFailed = false;
        long latencyMs = 0;
        if (jevGuard is not null)
        {
            using var timeout = new CancellationTokenSource(options.JevTimeout);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                jevAssessment = await jevGuard.AssessAsync(input, timeout.Token);
            }
            catch (Exception)
            {
                jevFailed = true; // timeouts and provider errors both count as Jev unavailable
            }
            finally
            {
                latencyMs = stopwatch.ElapsedMilliseconds;
            }
        }

        var decision = GuardPolicy.Decide(options, heuristicAssessment, jevAssessment, jevFailed);
        outcomes.Add(new GuardCaseResult(guardName, f.Id, f.Class, f.ExpectBlock, f.ExpectSignals,
            decision.Verdict, decision.FiredSignals, decision.Signals, latencyMs, jevFailed));
    }
    return outcomes;
}

// Per guard name: attack detection and flag rates, hard-negative false-block / false-flag rates (R29 bar: <= 1%),
// per-signal TP/FP/FN, latency, and -- only when a Jev guard supplied the scores -- per-signal threshold calibration.
void ReportGuard(List<GuardCaseResult> guardCases, bool jevSupplied = false)
{
    const string hardNegative = "hard_negative";
    const double falseBlockBar = 0.01;
    double[] thresholds = [0.5, 0.6, 0.7, 0.8, 0.85, 0.9, 0.95];

    foreach (var group in guardCases.GroupBy(r => r.Guard))
    {
        var cases = group.ToList();
        Console.WriteLine($"\n=== Guard eval: {group.Key} ({cases.Count} cases) ===");

        // Heuristic-only runs never block, so the flag rate (Blocked or Flagged) is the meaningful number there.
        foreach (var attackClass in cases.Where(r => r.Class != hardNegative).GroupBy(r => r.Class))
        {
            var total = attackClass.Count();
            var blocked = attackClass.Count(r => r.Verdict == GuardVerdict.Blocked);
            var flagged = attackClass.Count(r => r.Verdict is GuardVerdict.Blocked or GuardVerdict.Flagged);
            Console.WriteLine($"  {attackClass.Key,-18} n={total,3}  detection={Pct(blocked, total)}  flag={Pct(flagged, total)}");
        }

        var hard = cases.Where(r => r.Class == hardNegative).ToList();
        if (hard.Count > 0)
        {
            var falseBlocks = hard.Where(r => r.Verdict == GuardVerdict.Blocked).ToList();
            // A flag is wrong when the case expects no review-class signal at all.
            var falseFlags = hard.Where(r => r.Verdict == GuardVerdict.Flagged && r.ExpectSignals.Count == 0).ToList();
            var meetsBar = falseBlocks.Count / (double)hard.Count <= falseBlockBar;
            var barText = meetsBar ? "meets" : "FAILS";
            Console.WriteLine($"  hard negatives n={hard.Count}  false-block={Pct(falseBlocks.Count, hard.Count)} ({barText} <=1%)  false-flag={Pct(falseFlags.Count, hard.Count)}");
            foreach (var r in falseBlocks.Concat(falseFlags))
                Console.WriteLine($"    {r.Verdict} [{r.Id}] fired=[{string.Join(", ", r.FiredSignals)}]");
        }

        // A signal is predicted when it appears in FiredSignals.
        Console.WriteLine("  per signal:");
        var signalNames = cases.SelectMany(r => r.ExpectSignals)
            .Concat(cases.SelectMany(r => r.FiredSignals))
            .Distinct()
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        foreach (var signal in signalNames)
        {
            var tp = cases.Count(r => r.ExpectSignals.Contains(signal) && r.FiredSignals.Contains(signal));
            var fp = cases.Count(r => !r.ExpectSignals.Contains(signal) && r.FiredSignals.Contains(signal));
            var fn = cases.Count(r => r.ExpectSignals.Contains(signal) && !r.FiredSignals.Contains(signal));
            Console.WriteLine($"    {signal,-22} TP={tp,3} FP={fp,3} FN={fn,3}");
        }

        var jevFailures = cases.Count(r => r.JevFailed);
        if (jevFailures > 0)
            Console.WriteLine($"  jev failures (treated as unavailable): {jevFailures}");

        if (cases.Any(r => r.LatencyMs > 0))
        {
            var sortedLatency = cases.Select(r => r.LatencyMs).Order().ToList();
            Console.WriteLine($"  latency p50={Percentile(sortedLatency, 0.50)}ms p95={Percentile(sortedLatency, 0.95)}ms");
        }

        if (jevSupplied && hard.Count > 0)
        {
            Console.WriteLine("  calibration (lowest threshold with hard-negative false-block <= 1%; detection from scores):");
            foreach (var signal in GuardSignals.All.Where(s => s.Class == GuardSignalClass.Block))
            {
                var expectedPositives = cases.Count(r => r.Class != hardNegative && r.ExpectSignals.Contains(signal.Name));
                var hint = "no candidate threshold meets the bar";
                foreach (var threshold in thresholds)
                {
                    var hardFalseBlocks = hard.Count(r => ScoreOf(r, signal.Name) >= threshold);
                    if (hardFalseBlocks / (double)hard.Count > falseBlockBar) continue;

                    var detected = cases.Count(r => r.Class != hardNegative
                        && r.ExpectSignals.Contains(signal.Name)
                        && ScoreOf(r, signal.Name) >= threshold);
                    hint = $"threshold={threshold:F2} hard-neg false-block={Pct(hardFalseBlocks, hard.Count)} detection={Pct(detected, expectedPositives)}";
                    break;
                }
                Console.WriteLine($"    {signal.Name,-22} {hint}");
            }
        }
    }
}

static string Pct(int part, int whole) => whole == 0 ? "n/a" : $"{100.0 * part / whole:F1}%";

static double ScoreOf(GuardCaseResult r, string name) => r.Signals.TryGetValue(name, out var score) ? score : 0.0;

static long Percentile(IReadOnlyList<long> sorted, double p) => sorted[(int)Math.Ceiling(p * sorted.Count) - 1];

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

sealed record GuardCaseResult(string Guard, string Id, string Class, bool ExpectBlock, IReadOnlyList<string> ExpectSignals,
    GuardVerdict Verdict, IReadOnlyList<string> FiredSignals, IReadOnlyDictionary<string, double> Signals, long LatencyMs, bool JevFailed);

sealed class GuardFixture
{
    public string Id { get; set; } = "";
    public string Class { get; set; } = "";
    public string Message { get; set; } = "";
    public List<GuardHistoryTurn> History { get; set; } = new();
    public bool HasScreenshot { get; set; }
    public bool ExpectBlock { get; set; }
    public List<string> ExpectSignals { get; set; } = new();
}

sealed class GuardHistoryTurn
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}
