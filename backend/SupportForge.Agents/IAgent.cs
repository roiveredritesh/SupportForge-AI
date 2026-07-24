namespace SupportForge.Agents;

public interface IAgent
{
    string Name { get; }
    Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default);
}
