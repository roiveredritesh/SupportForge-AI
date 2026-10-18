using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using SupportForge.Agents;
using SupportForge.Agents.Guard;
using SupportForge.Agents.Jev;
using SupportForge.Agents.Triage;
using SupportForge.Agents.Verification;
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

// Retrieval-only mode (no LLM, no API key): code-retrieval <fixture.json> <repo checkout dir>. See CodeRetrievalEval.
// CODE_SEMANTIC=1 adds semantic code search, which needs the LLM provider's embeddings (Llm:ApiKey) and a vector store.
if (args is ["code-retrieval", var codeFixture, var codeRepoDir])
{
    (SupportForge.VectorStore.IVectorStoreService, ILlmEmbeddingClient)? semantic = null;
    if (Environment.GetEnvironmentVariable("CODE_SEMANTIC") == "1")
    {
        var semanticServices = new ServiceCollection();
        semanticServices.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
        semanticServices.AddLlmProviders(configuration);
        SupportForge.VectorStore.VectorStoreServiceCollectionExtensions.AddVectorStore(semanticServices, configuration);
        var semanticProvider = semanticServices.BuildServiceProvider();
        semantic = (semanticProvider.GetRequiredService<SupportForge.VectorStore.IVectorStoreService>(),
            semanticProvider.GetRequiredService<ILlmEmbeddingClient>());
    }
    return await CodeRetrievalEval.RunAsync(codeFixture, codeRepoDir,
        configuration.GetSection("Neo4j").Get<Neo4jOptions>() ?? new Neo4jOptions(), semantic);
}

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddLlmProviders(configuration); // same DI wiring SupportForge.Api uses -- one provider, config-selected
// Jev is registered only when Jev:ApiKey is set (U4). Without a key, the Jev runs below are skipped.
var jevConfigured = services.AddJev(configuration);
using var provider = services.BuildServiceProvider();

var llm = provider.GetRequiredService<ILlmChatClient>();
// Triage runs on the cheap tier, the same client production triage resolves (see LlmServiceCollectionExtensions.CheapTierKey).
var triageLlm = provider.GetRequiredKeyedService<ILlmChatClient>(LlmServiceCollectionExtensions.CheapTierKey);
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
var guardOptions = BindGuardOptions();
var guardResults = await RunGuardFixturesAsync("heuristic", null, guardOptions);
ReportGuard(guardResults);
// The Jev-backed guard runs only when Jev:ApiKey is set. It screens with the same options as the heuristic run.
if (jevConfigured)
    ReportGuard(await RunGuardFixturesAsync("jev", new JevInputGuard(provider.GetRequiredService<IJevClient>(), guardOptions), guardOptions), jevSupplied: true);

// Triage results are reported only and never affect the exit code: the eval is the measurement behind the R4 defaults
// (MinConfidence, MinMargin), so it is a baseline rather than a pass/fail gate. The thresholds are read from the report.
var triageResults = await RunTriageFixturesAsync("llm", () => new TriageAgent(triageLlm, loggers.CreateLogger<TriageAgent>()));
ReportTriage("llm", triageResults);

// The Jev triage run needs Jev:ApiKey. MinConfidence and MinMargin are zeroed so the decision policy does not turn
// low-confidence answers into "unclear" before the report sees them: the calibration table needs the raw Jev confidences.
// The LLM fallback on a Jev error or timeout still applies, as in production. JevTimeout keeps its class default (1.5s).
if (jevConfigured)
{
    var jevTriageOptions = new TriageOptions { Classifier = TriageClassifierMode.Jev, MinConfidence = 0, MinMargin = 0 };
    var jevTriageResults = await RunTriageFixturesAsync("jev", () => new TriageAgent(
        new LlmIntentClassifier(triageLlm),
        new JevIntentClassifier(provider.GetRequiredService<IJevClient>(), jevTriageOptions),
        jevTriageOptions,
        loggers.CreateLogger<TriageAgent>()));
    ReportTriage("jev", jevTriageResults);
}

// U10 findings relevance eval (R22): report-only, so it never affects the exit code. The LLM judge runs on the cheap tier,
// the same client production verifiers resolve.
var findingsJudge = new LlmFindingsJudge(triageLlm);
ReportSnippetRelevance(await RunSnippetRelevanceAsync("llm", findingsJudge));
ReportVisionRelevance(await RunVisionRelevanceAsync("llm", findingsJudge), findingsJudge.JudgesAllVisionFindings);

// U14 (R22): the Jev judge gets the same two runs when Jev:ApiKey is set. Code-graph snippets are sent too (IncludeCode = true),
// because these fixtures are synthetic, so no customer code leaves the building. Report-only, like the LLM run above.
if (jevConfigured)
{
    var jevJudge = new JevFindingsJudge(
        provider.GetRequiredService<IJevClient>(),
        new VerificationOptions { Jev = new VerificationJevOptions { IncludeCode = true } });
    ReportSnippetRelevance(await RunSnippetRelevanceAsync("jev", jevJudge));
    ReportVisionRelevance(await RunVisionRelevanceAsync("jev", jevJudge), jevJudge.JudgesAllVisionFindings);
}

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

// Options for both guard runs. They bind from the "Guard" section, and Mode is then forced to Enforce so the decision
// logic is always exercised, whatever the configured rollout mode is. GuardOptions is a plain class (not a record), so
// Mode is set on the freshly bound instance.
GuardOptions BindGuardOptions()
{
    var options = configuration.GetSection("Guard").Get<GuardOptions>() ?? new GuardOptions();
    options.Mode = GuardMode.Enforce;
    return options;
}

// U15 guard eval (docs/plans/2026-10-16-002-feat-jev-intent-classification-plan.md). Each fixture runs through the
// real HeuristicInputGuard (or the given Jev guard) and GuardPolicy.
async Task<List<GuardCaseResult>> RunGuardFixturesAsync(string guardName, IInputGuard? jevGuard, GuardOptions options)
{
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

            // Every signal at every candidate threshold. A hard negative counts against a signal only when it does not
            // expect that signal. "max hard-neg" is the highest score any such hard negative got, i.e. the margin left.
            Console.WriteLine("  threshold sweep per signal (hard-neg fire rate / detection on cases expecting the signal):");
            foreach (var signal in GuardSignals.All)
            {
                var negatives = hard.Where(r => !r.ExpectSignals.Contains(signal.Name)).ToList();
                var positives = cases.Where(r => r.Class != hardNegative && r.ExpectSignals.Contains(signal.Name)).ToList();
                var maxNegative = negatives.Count == 0 ? 0.0 : negatives.Max(r => ScoreOf(r, signal.Name));
                var cells = thresholds.Select(t =>
                    $"{t:F2}: {Pct(negatives.Count(r => ScoreOf(r, signal.Name) >= t), negatives.Count)}/{Pct(positives.Count(r => ScoreOf(r, signal.Name) >= t), positives.Count)}");
                Console.WriteLine($"    {signal.Name,-22} ({signal.Class}, n+={positives.Count}, max hard-neg={maxNegative:F2})  {string.Join("  ", cells)}");
            }
        }
    }
}

// U1 triage eval (docs/plans/2026-10-16-002-feat-jev-intent-classification-plan.md, R11). Each fixture runs through a real
// TriageAgent built by createAgent, so each classifier's run goes through the same policy as production. The LLM run uses
// the cheap tier. A per-case exception is recorded as "(error)" rather than thrown, so one provider error cannot abort the run.
async Task<List<TriageCaseResult>> RunTriageFixturesAsync(string classifierName, Func<TriageAgent> createAgent)
{
    var fixtures = LoadFixtures<TriageFixture>("triage-fixtures.json");
    var outcomes = new List<TriageCaseResult>();
    foreach (var f in fixtures)
    {
        var context = new AgentContext
        {
            ProjectId = "eval",
            Query = f.Query,
            ScreenshotBase64 = f.HasScreenshot ? "eval-screenshot" : null,
        };
        foreach (var turn in f.History) context.History.Add((turn.Role, turn.Content));

        var agent = createAgent();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await agent.RunAsync(context);
            stopwatch.Stop();
            // The raw decision is kept so ReportTriage can replay TriageDecisionPolicy at other thresholds without new calls.
            var decision = new IntentDecision(context.IntentScores, context.IntentConfidence ?? double.NaN, context.IntentSignals, 0, context.IntentSource);
            outcomes.Add(new TriageCaseResult(f.Id, f.Domain, f.Query, f.ExpectIntent, context.Intent, f.Ambiguous, f.History.Count > 0,
                context.IntentConfidence, context.IntentSource, stopwatch.ElapsedMilliseconds, context.TotalTokensUsed, null, decision, f.HasScreenshot));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            outcomes.Add(new TriageCaseResult(f.Id, f.Domain, f.Query, f.ExpectIntent, "(error)", f.Ambiguous, f.History.Count > 0,
                null, "", stopwatch.ElapsedMilliseconds, context.TotalTokensUsed, ex.Message, null, f.HasScreenshot));
        }
    }
    return outcomes;
}

// Accuracy (overall and on non-ambiguous cases), unclear rate on clear cases, per-label precision/recall, confusion pairs,
// every wrong case, latency, tokens, and calibration. Recommends MinConfidence only when confidence actually varies.
void ReportTriage(string classifierName, List<TriageCaseResult> cases)
{
    Console.WriteLine();
    Console.WriteLine($"=== Triage eval: {classifierName} ===");
    if (cases.Count == 0)
    {
        Console.WriteLine("  no fixtures loaded");
        return;
    }

    var correct = cases.Count(r => r.Correct);
    Console.WriteLine($"  overall accuracy: {Pct(correct, cases.Count)} ({correct}/{cases.Count})");

    var clear = cases.Where(r => !r.Ambiguous).ToList();
    var clearCorrect = clear.Count(r => r.Correct);
    var sentToUnclear = clear.Count(r => r.Actual == "unclear");
    Console.WriteLine($"  accuracy (non-ambiguous): {Pct(clearCorrect, clear.Count)} ({clearCorrect}/{clear.Count})");
    Console.WriteLine($"  unclear rate (non-ambiguous): {Pct(sentToUnclear, clear.Count)} ({sentToUnclear}/{clear.Count})");

    // The LLM classifier reads context.Query only, never the conversation, so follow-ups that only make sense with history
    // are expected to suffer. Reported separately as the baseline a history-aware classifier (plan R3) has to beat.
    var followUps = clear.Where(r => r.HasHistory).ToList();
    var followUpsCorrect = followUps.Count(r => r.Correct);
    Console.WriteLine($"  accuracy on follow-ups with history (non-ambiguous): {Pct(followUpsCorrect, followUps.Count)} ({followUpsCorrect}/{followUps.Count})");

    string[] labels = ["kb_question", "code_issue", "code_question", "screenshot_error", "greeting", "unclear"];
    Console.WriteLine("  per label (all cases):");
    foreach (var label in labels)
    {
        var support = cases.Count(r => r.Expected == label);
        var predicted = cases.Count(r => r.Actual == label);
        var truePositives = cases.Count(r => r.Expected == label && r.Actual == label);
        Console.WriteLine($"    {label,-18} support={support,3}  precision={Pct(truePositives, predicted)}  recall={Pct(truePositives, support)}");
    }

    Console.WriteLine("  confusion pairs (expected -> actual):");
    var confusions = cases.Where(r => !r.Correct)
        .GroupBy(r => (r.Expected, r.Actual))
        .OrderByDescending(g => g.Count())
        .ThenBy(g => g.Key.Expected, StringComparer.Ordinal)
        .ThenBy(g => g.Key.Actual, StringComparer.Ordinal)
        .ToList();
    if (confusions.Count == 0) Console.WriteLine("    none");
    foreach (var g in confusions)
        Console.WriteLine($"    {g.Key.Expected} -> {g.Key.Actual}: {g.Count()}");

    Console.WriteLine("  wrong cases:");
    foreach (var wrong in cases.Where(r => !r.Correct))
    {
        var error = wrong.Error is null ? "" : $" error=\"{wrong.Error}\"";
        Console.WriteLine($"    [{wrong.Id}] expected={wrong.Expected} actual={wrong.Actual}{error} query=\"{Truncate(wrong.Query, 80)}\"");
    }

    var latencies = cases.Select(r => r.LatencyMs).Order().ToList();
    Console.WriteLine($"  latency p50={Percentile(latencies, 0.50)}ms p95={Percentile(latencies, 0.95)}ms");
    Console.WriteLine($"  total tokens={cases.Sum(r => (long)r.Tokens)}");
    // Jev falls back to the LLM classifier on a timeout or error. Those cases carry the LLM's fixed confidence, so they are
    // counted here and left out of the calibration below.
    if (classifierName == "jev")
        Console.WriteLine($"  jev fallbacks: {cases.Count(r => r.Source == "llm-fallback")}");

    Console.WriteLine("  calibration (non-error cases by confidence):");
    var scored = cases.Where(r => r.Error is null).ToList();
    var confidenceVaries = scored.Any(r => r.Confidence is { } c && c != 1.0);
    if (!confidenceVaries)
    {
        Console.WriteLine("    the classifier reports no confidence (LLM classifier), so threshold recommendations need a confidence-reporting classifier");
        return;
    }

    var withConfidence = scored.Where(r => r.Confidence is not null && r.Source != "llm-fallback").ToList();
    string[] bucketNames = ["[0.00,0.50)", "[0.50,0.70)", "[0.70,0.85)", "[0.85,1.00]"];
    for (var b = 0; b < bucketNames.Length; b++)
    {
        var bucket = b;
        var inBucket = withConfidence.Where(r => ConfidenceBucket(r.Confidence!.Value) == bucket).ToList();
        var bucketCorrect = inBucket.Count(r => r.Correct);
        Console.WriteLine($"    {bucketNames[b]}  n={inBucket.Count,3}  accuracy={Pct(bucketCorrect, inBucket.Count)}");
    }

    // Lowest candidate where non-ambiguous cases at or above it are >= 95% accurate and at most 10% of non-ambiguous cases fall below it.
    double[] candidates = [0.5, 0.55, 0.6, 0.65, 0.7, 0.75, 0.8, 0.85, 0.9];
    var clearScored = withConfidence.Where(r => !r.Ambiguous).ToList();
    double? recommended = null;
    foreach (var t in candidates)
    {
        var above = clearScored.Where(r => r.Confidence!.Value >= t).ToList();
        if (above.Count == 0) continue;
        var belowShare = (clearScored.Count - above.Count) / (double)clearScored.Count;
        var aboveAccuracy = above.Count(r => r.Correct) / (double)above.Count;
        if (aboveAccuracy >= 0.95 && belowShare <= 0.10)
        {
            recommended = t;
            break;
        }
    }
    Console.WriteLine(recommended is { } threshold
        ? $"    recommended MinConfidence={threshold:F2}"
        : "    no threshold meets both bars (>= 95% accuracy at or above the threshold, <= 10% of non-ambiguous cases below it)");

    // Replays the production TriageDecisionPolicy on the recorded answers for each MinConfidence/MinMargin pair, so the
    // trade-off between clear-case accuracy and catching the ambiguous fixtures is read from one run. Fallbacks are left out.
    var replayable = withConfidence.Where(r => r.Decision is not null).ToList();
    Console.WriteLine($"  policy sweep (TriageDecisionPolicy replayed on {replayable.Count} recorded answers):");
    foreach (var minConfidence in new[] { 0.0, 0.5, 0.6, 0.7, 0.8, 0.85, 0.9, 0.95 })
    {
        foreach (var minMargin in new[] { 0.0, 0.1, 0.15, 0.2, 0.3, 0.5 })
        {
            var sweepOptions = new TriageOptions { MinConfidence = minConfidence, MinMargin = minMargin };
            var replayed = replayable.Select(r => (Case: r, Intent: TriageDecisionPolicy.Decide(r.Decision!, r.HasScreenshot, sweepOptions).Intent)).ToList();
            var clearReplayed = replayed.Where(x => !x.Case.Ambiguous).ToList();
            var unclearExpected = replayed.Where(x => x.Case.Expected == TriageLabels.Unclear).ToList();
            Console.WriteLine($"    MinConfidence={minConfidence:F2} MinMargin={minMargin:F2}"
                + $"  clear accuracy={Pct(clearReplayed.Count(x => x.Intent == x.Case.Expected), clearReplayed.Count)}"
                + $"  clear->unclear={Pct(clearReplayed.Count(x => x.Intent == TriageLabels.Unclear), clearReplayed.Count)}"
                + $"  unclear caught={Pct(unclearExpected.Count(x => x.Intent == TriageLabels.Unclear), unclearExpected.Count)}"
                + $"  overall={Pct(replayed.Count(x => x.Intent == x.Case.Expected), replayed.Count)}");
        }
    }
}

// U10 findings relevance (docs/plans/2026-10-16-002-feat-jev-intent-classification-plan.md, R22). Each mixed-relevance fixture goes
// through the judge's JudgeSnippetsAsync. A throwing case is recorded with Error and left out of the metrics, so one provider
// failure cannot abort the run or count as a rejected decoy.
async Task<List<RelevanceCaseResult>> RunSnippetRelevanceAsync(string judgeName, IFindingsJudge judge)
{
    var fixtures = LoadFixtures<SnippetRelevanceFixture>("snippet-relevance-fixtures.json");
    var outcomes = new List<RelevanceCaseResult>();
    foreach (var f in fixtures)
    {
        var context = new AgentContext { ProjectId = "eval", Query = f.Query };
        var kind = f.Kind == "code" ? FindingsKind.Code : FindingsKind.Kb;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var judgement = await judge.JudgeSnippetsAsync(context, kind, f.Snippets, CancellationToken.None);
            stopwatch.Stop();
            outcomes.Add(new RelevanceCaseResult(judgeName, f.Id, f.Kind, f.Query, f.Relevant, PredictedSnippets(judgement),
                judgement.Scores, judgement.Coverage, f.Coverage, stopwatch.ElapsedMilliseconds, null));
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            outcomes.Add(new RelevanceCaseResult(judgeName, f.Id, f.Kind, f.Query, f.Relevant, [], null, null, f.Coverage,
                stopwatch.ElapsedMilliseconds, ex.Message));
        }
    }
    return outcomes;
}

// Report-only (R22), per kind: pass/fail agreement, per-snippet precision and recall, recall on multi-relevant cases (the number a
// single-pick judge is capped on), hit rate on buried cases, decoy rejection, coverage accuracy and score-threshold sweeps when the
// judge supplies them, then every disagreeing case. Predicted sets come from PredictedSnippets, so a single-pick judge is read as one
// index and a score-based judge at 0.5.
void ReportSnippetRelevance(List<RelevanceCaseResult> snippetResults)
{
    Console.WriteLine();
    Console.WriteLine($"=== Findings relevance eval: {snippetResults.FirstOrDefault()?.Judge ?? "(no judge)"} ===");
    if (snippetResults.Count == 0)
    {
        Console.WriteLine("  no fixtures loaded");
        return;
    }

    foreach (var kind in new[] { "kb", "code" })
    {
        var kindResults = snippetResults.Where(r => r.Kind == kind).ToList();
        var cases = kindResults.Where(r => r.Error is null).ToList();
        Console.WriteLine($"\n  {kind}: n={cases.Count} errors={kindResults.Count - cases.Count}");
        if (cases.Count == 0) continue;

        var agreed = cases.Count(r => (r.Predicted.Count > 0) == (r.Expected.Count > 0));
        Console.WriteLine($"    pass/fail agreement: {Pct(agreed, cases.Count)} ({agreed}/{cases.Count})");

        var hits = cases.Sum(r => Hits(r.Predicted, r.Expected));
        var predictedTotal = cases.Sum(r => r.Predicted.Count);
        var expectedTotal = cases.Sum(r => r.Expected.Count);
        Console.WriteLine($"    per-snippet precision={Pct(hits, predictedTotal)} recall={Pct(hits, expectedTotal)}");

        var multi = cases.Where(r => r.Expected.Count >= 2).ToList();
        if (multi.Count > 0)
        {
            var multiHits = multi.Sum(r => Hits(r.Predicted, r.Expected));
            var multiExpected = multi.Sum(r => r.Expected.Count);
            Console.WriteLine($"    recall on multi-relevant cases: {Pct(multiHits, multiExpected)} ({multiHits}/{multiExpected} snippets over {multi.Count} cases)");
            Console.WriteLine($"      single-pick cap: a judge that names one snippet per case cannot exceed {Pct(multi.Count, multiExpected)} recall here");
        }

        // Buried = the only relevant snippet sits at index 3 or later, behind decoys. Multi-relevant cases are excluded so this
        // number isolates position, not the single-pick cap above.
        var buried = cases.Where(r => r.Expected.Count == 1 && r.Expected[0] >= 3).ToList();
        if (buried.Count > 0)
        {
            var buriedFound = buried.Count(r => r.Predicted.Contains(r.Expected[0]));
            Console.WriteLine($"    hit rate on buried cases (only relevant snippet at index 3 or later): {Pct(buriedFound, buried.Count)} ({buriedFound}/{buried.Count})");
        }

        var decoys = cases.Where(r => r.Expected.Count == 0).ToList();
        if (decoys.Count > 0)
        {
            var rejected = decoys.Count(r => r.Predicted.Count == 0);
            Console.WriteLine($"    decoy rejection: {Pct(rejected, decoys.Count)} ({rejected}/{decoys.Count})");
        }

        var covered = cases.Where(r => r.Coverage is not null).ToList();
        if (covered.Count > 0)
        {
            var coverageCorrect = covered.Count(r => CoverageLevel(r.Coverage!.Value) == r.ExpectedCoverage);
            Console.WriteLine($"    coverage accuracy: {Pct(coverageCorrect, covered.Count)} ({coverageCorrect}/{covered.Count})");

            // Verification:PartialCoverageBelow: a passing branch gets the partial-coverage hint when coverage is below the cut.
            // Read as: share of expected-partial cases that get the hint, and of expected-complete cases that get it wrongly.
            var partialCases = covered.Where(r => r.ExpectedCoverage == "partial").ToList();
            var completeCases = covered.Where(r => r.ExpectedCoverage == "complete").ToList();
            Console.WriteLine($"    PartialCoverageBelow sweep (partial flagged / complete wrongly flagged; n={partialCases.Count}/{completeCases.Count}):");
            foreach (var cut in new[] { 0.5, 0.6, 0.67, 0.75, 0.85, 0.95 })
                Console.WriteLine($"      below {cut:F2}: {Pct(partialCases.Count(r => r.Coverage!.Value < cut), partialCases.Count)} / {Pct(completeCases.Count(r => r.Coverage!.Value < cut), completeCases.Count)}");
        }
        else
        {
            Console.WriteLine("    coverage: the judge reports no coverage");
        }

        var scored = cases.Where(r => r.Scores is not null).ToList();
        if (scored.Count > 0)
        {
            Console.WriteLine("    per-snippet precision/recall at score thresholds:");
            foreach (var threshold in new[] { 0.3, 0.5, 0.7 })
            {
                var rows = scored.Select(r => (Predicted: PredictedAtThreshold(r.Scores!, threshold), Expected: r.Expected)).ToList();
                var rowHits = rows.Sum(row => Hits(row.Predicted, row.Expected));
                var rowPredicted = rows.Sum(row => row.Predicted.Count);
                var rowExpected = rows.Sum(row => row.Expected.Count);
                Console.WriteLine($"      threshold {threshold:F1}: precision={Pct(rowHits, rowPredicted)} recall={Pct(rowHits, rowExpected)}");
            }
        }

        Console.WriteLine("    disagreements (predicted set differs from expected):");
        var disagreements = cases.Where(r => !SameSet(r.Predicted, r.Expected)).ToList();
        if (disagreements.Count == 0) Console.WriteLine("      none");
        foreach (var row in disagreements)
            Console.WriteLine($"      [{row.Id}] expected=[{string.Join(",", row.Expected)}] predicted=[{string.Join(",", row.Predicted)}] query=\"{Truncate(row.Query, 80)}\"");
    }

    var latencies = snippetResults.Select(r => r.LatencyMs).Order().ToList();
    Console.WriteLine($"\n  latency p50={Percentile(latencies, 0.50)}ms p95={Percentile(latencies, 0.95)}ms");
    foreach (var failed in snippetResults.Where(r => r.Error is not null))
        Console.WriteLine($"  ERROR [{failed.Id}] {failed.Error}");
}

// U10 vision relevance (R22), report-only. The real VisionAnalyzerVerifier runs on each fixture, with the screenshot placeholder the
// verifier needs to run at all. A case that throws is printed and skipped, because VisionRelevanceResult has no error field.
async Task<List<VisionRelevanceResult>> RunVisionRelevanceAsync(string judgeName, IFindingsJudge judge)
{
    var fixtures = LoadFixtures<VisionRelevanceFixture>("vision-relevance-fixtures.json");
    var outcomes = new List<VisionRelevanceResult>();
    foreach (var f in fixtures)
    {
        var findings = f.Findings ?? "";
        var context = new AgentContext { ProjectId = "eval", Query = f.Query, ScreenshotBase64 = "eval-screenshot" };
        context.VisionFindings = findings;

        var verifier = new VisionAnalyzerVerifier(judge, loggers.CreateLogger<VisionAnalyzerVerifier>());
        try
        {
            await verifier.RunAsync(context);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR [{f.Id}] {ex.Message}");
            continue;
        }

        var actualRelevant = context.VisionVerification.Status == VerificationStatus.Passed;
        var score = context.VisionVerification.Scores is { Count: > 0 } scores ? scores[0] : (double?)null;
        outcomes.Add(new VisionRelevanceResult(judgeName, f.Id, f.ExpectRelevant, actualRelevant, findings.Length >= 20, f.Query, score));
    }
    return outcomes;
}

// Report-only (R22). The headline is long irrelevant findings: today's verifier judges only findings under 20 characters, so anything
// longer passes without an LLM call. These are the cases this fixture set exists to expose.
// judgesAllFindings comes from the judge itself: the LLM judge keeps the under-20-characters gate, the Jev judge is asked about every finding.
void ReportVisionRelevance(List<VisionRelevanceResult> visionResults, bool judgesAllFindings)
{
    Console.WriteLine();
    Console.WriteLine($"=== Vision findings relevance eval: {visionResults.FirstOrDefault()?.Judge ?? "(no judge)"} ===");
    if (visionResults.Count == 0)
    {
        Console.WriteLine("  no fixtures loaded");
        return;
    }

    var agreed = visionResults.Count(r => r.Agreed);
    Console.WriteLine($"  agreement: {Pct(agreed, visionResults.Count)} ({agreed}/{visionResults.Count})");

    var longIrrelevant = visionResults.Where(r => !r.Expected && r.LongFindings).ToList();
    var longPassed = longIrrelevant.Count(r => r.Actual);
    Console.WriteLine($"  long irrelevant findings passed: {longPassed}/{longIrrelevant.Count}");
    Console.WriteLine(judgesAllFindings
        ? "    note: this judge is asked about every finding, so long findings are judged too"
        : "    note: today's verifier only judges findings under 20 characters; longer findings pass without an LLM call");

    foreach (var row in visionResults.Where(r => !r.Agreed))
        Console.WriteLine($"  DISAGREE [{row.Id}] expected={row.Expected} actual={row.Actual} longFindings={row.LongFindings} query=\"{Truncate(row.Query, 80)}\"");

    // Verification:MinRelevance is also the vision cut-off, so the same candidates are swept on the judge's score when it reports one.
    var visionScored = visionResults.Where(r => r.Score is not null).ToList();
    if (visionScored.Count > 0)
    {
        Console.WriteLine("  agreement at score thresholds:");
        foreach (var threshold in new[] { 0.3, 0.5, 0.7 })
            Console.WriteLine($"    threshold {threshold:F1}: {Pct(visionScored.Count(r => (r.Score!.Value >= threshold) == r.Expected), visionScored.Count)}");
    }
}

// The predicted set for the headline numbers: a score-based judge is read at 0.5, a single-pick judge at its one index.
static IReadOnlyList<int> PredictedSnippets(SnippetJudgement judgement)
{
    if (judgement.Scores is not null) return PredictedAtThreshold(judgement.Scores, 0.5);
    if (judgement.BestIndex is { } best) return new List<int> { best };
    return [];
}

static List<int> PredictedAtThreshold(IReadOnlyList<double> scores, double threshold) =>
    Enumerable.Range(0, scores.Count).Where(i => scores[i] >= threshold).ToList();

static int Hits(IReadOnlyList<int> predicted, IReadOnlyList<int> expected) => predicted.Count(i => expected.Contains(i));

static bool SameSet(IReadOnlyList<int> a, IReadOnlyList<int> b) => a.Count == b.Count && a.Order().SequenceEqual(b.Order());

// U14: the bands for a normalised 0..1 Coverage (Jev's coverage score / 2): none below 0.25, partial from 0.25 to 0.75, complete above 0.75.
static string CoverageLevel(double coverage) => coverage < 0.25 ? "none" : coverage <= 0.75 ? "partial" : "complete";

static int ConfidenceBucket(double confidence) => confidence < 0.5 ? 0 : confidence < 0.7 ? 1 : confidence < 0.85 ? 2 : 3;

static string Pct(int part, int whole) => whole == 0 ? "n/a" : $"{100.0 * part / whole:F1}%";

static double ScoreOf(GuardCaseResult r, string name) => r.Signals.TryGetValue(name, out var score) ? score : 0.0;

static long Percentile(IReadOnlyList<long> sorted, double p) => sorted[(int)Math.Ceiling(p * sorted.Count) - 1];

sealed record CaseResult(string Verifier, string Id, string Domain, string Query, bool Expected, bool Actual, string? Reason)
{
    public bool Agreed => Expected == Actual;
}

sealed record TriageCaseResult(string Id, string Domain, string Query, string Expected, string Actual, bool Ambiguous, bool HasHistory, double? Confidence,
    string Source, long LatencyMs, int Tokens, string? Error, IntentDecision? Decision, bool HasScreenshot)
{
    public bool Correct => Expected == Actual;
}

sealed record RelevanceCaseResult(string Judge, string Id, string Kind, string Query, IReadOnlyList<int> Expected, IReadOnlyList<int> Predicted,
    IReadOnlyList<double>? Scores, double? Coverage, string ExpectedCoverage, long LatencyMs, string? Error);

sealed record VisionRelevanceResult(string Judge, string Id, bool Expected, bool Actual, bool LongFindings, string Query, double? Score)
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

sealed class TriageFixture
{
    public string Id { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Query { get; set; } = "";
    public List<TriageHistoryTurn> History { get; set; } = new();
    public bool HasScreenshot { get; set; }
    public string ExpectIntent { get; set; } = "";
    public bool Ambiguous { get; set; }
}

sealed class TriageHistoryTurn
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

sealed class SnippetRelevanceFixture
{
    public string Id { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Query { get; set; } = "";
    public List<string> Snippets { get; set; } = new();
    public List<int> Relevant { get; set; } = new();
    public string Coverage { get; set; } = "";
}

sealed class VisionRelevanceFixture
{
    public string Id { get; set; } = "";
    public string Query { get; set; } = "";
    public string Findings { get; set; } = "";
    public bool ExpectRelevant { get; set; }
}
