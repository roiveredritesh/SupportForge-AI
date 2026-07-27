using System.Collections.Concurrent;
using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class CoordinatorPipelineTests
{
    [Fact]
    public async Task RunAsync_RunsTriageFirstAndDrafterLast_WithKbCodeVisionBetween()
    {
        var order = new ConcurrentQueue<string>();
        var triage = new RecordingAgent("Triage", order);
        var kb = new RecordingAgent("KbResearcher", order);
        var code = new RecordingAgent("CodeAnalyzer", order);
        var vision = new RecordingAgent("VisionAnalyzer", order);
        var drafter = new RecordingAgent("Drafter", order);

        var pipeline = new CoordinatorPipeline(
            triage,
            kb, new PassingVerifier("KbResearcherVerifier"),
            code, new PassingVerifier("CodeAnalyzerVerifier"),
            vision, new PassingVerifier("VisionAnalyzerVerifier"),
            drafter);
        await pipeline.RunAsync(new AgentContext { ProjectId = "proj1", Query = "test" });

        var recorded = order.ToList();
        Assert.Equal(5, recorded.Count);
        Assert.Equal("Triage", recorded[0]);
        Assert.Equal("Drafter", recorded[^1]);
        Assert.Equal(
            new HashSet<string> { "KbResearcher", "CodeAnalyzer", "VisionAnalyzer" },
            recorded.Skip(1).Take(3).ToHashSet());
    }

    [Fact]
    public async Task RunAsync_MergesSourcesFromConcurrentBranches_WithoutLosingEntries()
    {
        var order = new ConcurrentQueue<string>();
        var triage = new RecordingAgent("Triage", order);
        var kb = new SourceAddingAgent("KbResearcher", "KB", 50);
        var code = new SourceAddingAgent("CodeAnalyzer", "Code", 50);
        var vision = new RecordingAgent("VisionAnalyzer", order);
        var drafter = new RecordingAgent("Drafter", order);

        var pipeline = new CoordinatorPipeline(
            triage,
            kb, new PassingVerifier("KbResearcherVerifier"),
            code, new PassingVerifier("CodeAnalyzerVerifier"),
            vision, new PassingVerifier("VisionAnalyzerVerifier"),
            drafter);
        var result = await pipeline.RunAsync(new AgentContext { ProjectId = "proj1", Query = "test" });

        Assert.Equal(100, result.Sources.Count);
        Assert.Equal(50, result.Sources.Count(s => s.Label.StartsWith("KB")));
        Assert.Equal(50, result.Sources.Count(s => s.Label.StartsWith("Code")));
    }

    [Fact]
    public async Task RunAsync_VerifierRequestsRetry_SpecialistRunsAgainBeforeDrafter()
    {
        var kbRunCount = 0;
        var kb = new CountingKbAgent(() => kbRunCount++);
        var code = new RecordingAgent("CodeAnalyzer", new ConcurrentQueue<string>());
        var vision = new RecordingAgent("VisionAnalyzer", new ConcurrentQueue<string>());
        var triage = new RecordingAgent("Triage", new ConcurrentQueue<string>());
        var drafterRan = false;
        var drafter = new CountingAgent("Drafter", () => drafterRan = true);

        // KbVerifier fails exactly once (Attempts becomes 1), forcing one retry, then passes.
        var kbVerifier = new RetryOnceVerifier(v => v.KbVerification, "KbResearcherVerifier");
        var codeVerifier = new PassingVerifier("CodeAnalyzerVerifier");
        var visionVerifier = new PassingVerifier("VisionAnalyzerVerifier");

        var pipeline = new CoordinatorPipeline(triage, kb, kbVerifier, code, codeVerifier, vision, visionVerifier, drafter);
        var result = await pipeline.RunAsync(new AgentContext { ProjectId = "proj1", Query = "test" });

        Assert.Equal(2, kbRunCount); // ran once, failed verification, ran again
        Assert.Equal(VerificationStatus.Passed, result.KbVerification.Status);
        Assert.True(drafterRan);
    }

    private sealed class RecordingAgent : IAgent
    {
        private readonly ConcurrentQueue<string> _order;
        public string Name { get; }
        public RecordingAgent(string name, ConcurrentQueue<string> order) { Name = name; _order = order; }
        public Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
        {
            _order.Enqueue(Name);
            return Task.FromResult(context);
        }
    }

    private sealed class CountingAgent : IAgent
    {
        private readonly Action _onRun;
        public string Name { get; }
        public CountingAgent(string name, Action onRun) { Name = name; _onRun = onRun; }
        public Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
        {
            _onRun();
            return Task.FromResult(context);
        }
    }

    private sealed class CountingKbAgent : IAgent
    {
        private readonly Action _onRun;
        public string Name => "KbResearcher";
        public CountingKbAgent(Action onRun) => _onRun = onRun;
        public Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
        {
            _onRun();
            context.KbVerification.Attempts++;
            return Task.FromResult(context);
        }
    }

    private sealed class SourceAddingAgent : IAgent
    {
        private readonly string _labelPrefix;
        private readonly int _count;
        public string Name { get; }
        public SourceAddingAgent(string name, string labelPrefix, int count)
        {
            Name = name;
            _labelPrefix = labelPrefix;
            _count = count;
        }

        public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
        {
            for (var i = 0; i < _count; i++)
            {
                await Task.Yield();
                context.Sources.Add(($"{_labelPrefix} {i}", $"url-{_labelPrefix}-{i}"));
            }
            return context;
        }
    }

    // A verifier stub that always marks its branch Passed — used for branches not under test.
    private sealed class PassingVerifier : IAgent
    {
        public string Name { get; }
        public PassingVerifier(string name = "PassingVerifier") => Name = name;
        public Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
        {
            context.KbVerification.Status = context.KbVerification.Status == VerificationStatus.NotRun ? VerificationStatus.Passed : context.KbVerification.Status;
            context.CodeVerification.Status = context.CodeVerification.Status == VerificationStatus.NotRun ? VerificationStatus.Passed : context.CodeVerification.Status;
            context.VisionVerification.Status = context.VisionVerification.Status == VerificationStatus.NotRun ? VerificationStatus.Passed : context.VisionVerification.Status;
            return Task.FromResult(context);
        }
    }

    // Fails the targeted branch's first attempt (Attempts becomes 1 -> FailedRetrying), passes on retry (Attempts becomes 2).
    private sealed class RetryOnceVerifier : IAgent
    {
        private readonly Func<AgentContext, VerificationResult> _select;
        public string Name { get; }
        public RetryOnceVerifier(Func<AgentContext, VerificationResult> select, string name = "RetryOnceVerifier")
        {
            _select = select;
            Name = name;
        }
        public Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
        {
            var v = _select(context);
            v.Status = v.Attempts >= 2 ? VerificationStatus.Passed : VerificationStatus.FailedRetrying;
            return Task.FromResult(context);
        }
    }
}
