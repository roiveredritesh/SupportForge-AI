using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using SupportForge.Agents;
using Xunit;

// PipelineTelemetry.ActivitySource is process-global (correctly, for production). xUnit runs
// different test classes in parallel by default, and any other test that exercises the real
// CoordinatorPipeline (e.g. ChatControllerTests) would leak its spans into these tests' in-memory
// exporter if it ran concurrently. Disabling assembly-wide parallelization removes that cross-talk;
// this suite is small enough (well under a minute) that running it serially is a fine trade-off.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SupportForge.Api.Tests.Agents;

public class TracingTests
{
    private sealed class FakeAgent : IAgent
    {
        private readonly Action<AgentContext>? _mutate;
        private readonly bool _throws;

        public FakeAgent(string name, Action<AgentContext>? mutate = null, bool throws = false)
        {
            Name = name;
            _mutate = mutate;
            _throws = throws;
        }

        public string Name { get; }

        public Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
        {
            if (_throws) throw new InvalidOperationException($"{Name} failed");
            _mutate?.Invoke(context);
            return Task.FromResult(context);
        }
    }

    private static CoordinatorPipeline MakePipeline(bool visionThrows = false) => new(
        new FakeAgent("Triage"),
        new FakeAgent("KbResearcher"), new FakeAgent("KbResearcherVerifier"),
        new FakeAgent("CodeAnalyzer"), new FakeAgent("CodeAnalyzerVerifier"),
        new FakeAgent("VisionAnalyzer", throws: visionThrows), new FakeAgent("VisionAnalyzerVerifier"),
        new FakeAgent("Drafter"));

    private static List<Activity> CollectSpans(Action run)
    {
        var spans = new List<Activity>();
        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(PipelineTelemetry.ActivitySourceName)
            .AddInMemoryExporter(spans)
            .Build();

        run();
        provider.ForceFlush();
        return spans;
    }

    [Fact]
    public void FullPipelineRun_ProducesOneSpanPerAgentThatRan_CorrectlyOrdered()
    {
        var pipeline = MakePipeline();
        var spans = CollectSpans(() =>
            pipeline.RunAsync(new AgentContext { ProjectId = "p", Query = "q" }).GetAwaiter().GetResult());

        // All 8 nodes in the fixed fan-out/fan-in graph execute (per-agent no-op logic lives inside
        // each agent's own RunAsync, not the graph shape -- see docs/agentic-pipeline.md, U10).
        var names = spans.Select(s => s.OperationName).ToList();
        Assert.Equal(8, names.Count);
        Assert.Contains("Triage", names);
        Assert.Contains("Drafter", names);
        Assert.All(spans, s => Assert.Equal(ActivityStatusCode.Ok, s.Status));
    }

    [Fact]
    public void AgentThatThrows_ClosesSpanWithErrorStatus_RatherThanLeavingItOpen()
    {
        var pipeline = MakePipeline(visionThrows: true);
        var spans = CollectSpans(() =>
            Assert.ThrowsAny<Exception>(() =>
                pipeline.RunAsync(new AgentContext { ProjectId = "p", Query = "q" }).GetAwaiter().GetResult()));

        var visionSpan = Assert.Single(spans, s => s.OperationName == "VisionAnalyzer");
        Assert.Equal(ActivityStatusCode.Error, visionSpan.Status);
        Assert.False(visionSpan.Duration == TimeSpan.Zero); // closed, not left open
    }
}
