using Microsoft.Agents.AI.Workflows;

namespace SupportForge.Agents;

/// <summary>Adapts an <see cref="IAgent"/> into a Microsoft Agent Framework workflow node.</summary>
internal sealed class AgentExecutor : Executor<AgentContext, AgentContext>
{
    private readonly IAgent _agent;
    private readonly bool _isTerminal;

    public AgentExecutor(IAgent agent, bool isTerminal = false) : base(agent.Name)
    {
        _agent = agent;
        _isTerminal = isTerminal;
    }

    public override async ValueTask<AgentContext> HandleAsync(
        AgentContext message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var result = await _agent.RunAsync(message, cancellationToken);
        if (_isTerminal)
            await context.YieldOutputAsync(result, cancellationToken);
        return result;
    }
}

/// <summary>
/// Fan-in target for KbResearcher/CodeAnalyzer/VisionAnalyzer. For in-process execution, a fan-in
/// barrier delivers one <see cref="HandleAsync"/> call per source (not a single batched array);
/// <see cref="OnMessageDeliveryFinishedAsync"/> fires once all sources for the step have been
/// delivered, which is where the merged result is forwarded on. All three branches mutate the
/// same shared <see cref="AgentContext"/> reference, so "merging" is just forwarding it once.
/// </summary>
[SendsMessage(typeof(AgentContext))]
internal sealed class MergeExecutor : Executor<AgentContext>
{
    private AgentContext? _received;

    public MergeExecutor() : base("Merge") { }

    public override ValueTask HandleAsync(
        AgentContext message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        _received = message;
        return default;
    }

    protected override ValueTask OnMessageDeliveryFinishedAsync(
        IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var result = _received ?? throw new InvalidOperationException("Merge fired with no delivered messages.");
        _received = null;
        return context.SendMessageAsync(result, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Runs the support-query pipeline as a Microsoft Agent Framework workflow:
/// <see href="https://github.com/microsoft/agent-framework"/>. Triage runs first; KbResearcher,
/// CodeAnalyzer, and VisionAnalyzer fan out in parallel (they don't depend on each other's
/// output, only on Triage's Intent); results fan back in before Drafter runs.
/// </summary>
public sealed class CoordinatorPipeline
{
    private readonly Workflow _workflow;

    public CoordinatorPipeline(IAgent triage, IAgent kb, IAgent code, IAgent vision, IAgent drafter)
    {
        var triageExec = new AgentExecutor(triage);
        var kbExec = new AgentExecutor(kb);
        var codeExec = new AgentExecutor(code);
        var visionExec = new AgentExecutor(vision);
        var mergeExec = new MergeExecutor();
        var drafterExec = new AgentExecutor(drafter, isTerminal: true);

        var parallelExecs = new ExecutorBinding[] { kbExec, codeExec, visionExec };

        var builder = new WorkflowBuilder(triageExec);
        builder.AddFanOutEdge(triageExec, parallelExecs);
        builder.AddFanInBarrierEdge(parallelExecs, mergeExec);
        builder.AddEdge(mergeExec, drafterExec);

        _workflow = builder.WithOutputFrom(drafterExec).Build();
    }

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(_workflow, context, cancellationToken: ct);
        await foreach (var evt in run.WatchStreamAsync(ct))
        {
            switch (evt)
            {
                case WorkflowOutputEvent { Data: AgentContext result }:
                    return result;
                case WorkflowErrorEvent { Exception: { } ex }:
                    throw new InvalidOperationException("Workflow failed.", ex);
                case ExecutorFailedEvent failed:
                    throw new InvalidOperationException($"Executor '{failed.ExecutorId}' failed: {failed.Data}");
            }
        }

        throw new InvalidOperationException("Workflow completed without producing output.");
    }
}
