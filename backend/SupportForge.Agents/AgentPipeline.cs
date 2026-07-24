namespace SupportForge.Agents;

public sealed class AgentPipeline
{
    private readonly IReadOnlyList<IAgent> _agents;

    public AgentPipeline(IEnumerable<IAgent> agents) => _agents = agents.ToList();

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        foreach (var agent in _agents)
            context = await agent.RunAsync(context, ct);

        return context;
    }
}
