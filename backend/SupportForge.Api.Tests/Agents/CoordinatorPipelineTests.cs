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

        var pipeline = new CoordinatorPipeline(triage, kb, code, vision, drafter);
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

        var pipeline = new CoordinatorPipeline(triage, kb, code, vision, drafter);
        var result = await pipeline.RunAsync(new AgentContext { ProjectId = "proj1", Query = "test" });

        Assert.Equal(100, result.Sources.Count);
        Assert.Equal(50, result.Sources.Count(s => s.Label.StartsWith("KB")));
        Assert.Equal(50, result.Sources.Count(s => s.Label.StartsWith("Code")));
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
                await Task.Yield(); // encourage interleaving with the other concurrent branch
                context.Sources.Add(($"{_labelPrefix} {i}", $"url-{_labelPrefix}-{i}"));
            }
            return context;
        }
    }
}
