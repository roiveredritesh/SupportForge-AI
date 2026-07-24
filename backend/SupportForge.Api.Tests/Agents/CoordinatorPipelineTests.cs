using SupportForge.Agents;
using Xunit;

namespace SupportForge.Api.Tests.Agents;

public class CoordinatorPipelineTests
{
    [Fact]
    public async Task RunAsync_RunsAgentsInOrder_AndRecordsExecutionOrder()
    {
        var order = new List<string>();
        var agents = new IAgent[]
        {
            new RecordingAgent("Triage", order),
            new RecordingAgent("KbResearcher", order),
            new RecordingAgent("CodeAnalyzer", order),
            new RecordingAgent("VisionAnalyzer", order),
            new RecordingAgent("Drafter", order),
        };

        var pipeline = new CoordinatorPipeline(agents);
        await pipeline.RunAsync(new AgentContext { ProjectId = "proj1", Query = "test" });

        Assert.Equal(new[] { "Triage", "KbResearcher", "CodeAnalyzer", "VisionAnalyzer", "Drafter" }, order);
    }

    private sealed class RecordingAgent : IAgent
    {
        private readonly List<string> _order;
        public string Name { get; }
        public RecordingAgent(string name, List<string> order) { Name = name; _order = order; }
        public Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
        {
            _order.Add(Name);
            return Task.FromResult(context);
        }
    }
}
