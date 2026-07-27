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
/// Identity node sitting between a verifier's "not retrying" conditional edge and the fan-in barrier
/// into Merge. A barrier's "wait for one message per source" only works when that source's edge into
/// the barrier is unconditional; a verifier's retry-vs-forward decision must stay conditional. This
/// collector is invoked exactly once per branch's full run (the retry-bound message never reaches it,
/// it goes back to the specialist instead), so it's what the barrier can safely wait on.
/// </summary>
internal sealed class PassthroughExecutor : Executor<AgentContext, AgentContext>
{
    public PassthroughExecutor(string id) : base(id) { }
    public override ValueTask<AgentContext> HandleAsync(
        AgentContext message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(message);
}

/// <summary>
/// Fan-in target for the three branch collectors. For in-process execution, a fan-in barrier delivers
/// one <see cref="HandleAsync"/> call per source (not a single batched array);
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
/// CodeAnalyzer, and VisionAnalyzer fan out in parallel. Each specialist is followed by its verifier,
/// which either loops back to the specialist for one retry (conditional edge on
/// VerificationStatus.FailedRetrying) or forwards to that branch's collector. The three collectors
/// feed a fan-in barrier into Merge, which only requires each collector to eventually deliver exactly
/// one message — regardless of how many retry rounds its branch took. Drafter runs last.
/// </summary>
public sealed class CoordinatorPipeline
{
    private readonly Workflow _workflow;

    public CoordinatorPipeline(
        IAgent triage,
        IAgent kb, IAgent kbVerifier,
        IAgent code, IAgent codeVerifier,
        IAgent vision, IAgent visionVerifier,
        IAgent drafter)
    {
        var triageExec = new AgentExecutor(triage);

        var kbExec = new AgentExecutor(kb);
        var kbVerifierExec = new AgentExecutor(kbVerifier);
        var kbCollectorExec = new PassthroughExecutor("KbCollector");

        var codeExec = new AgentExecutor(code);
        var codeVerifierExec = new AgentExecutor(codeVerifier);
        var codeCollectorExec = new PassthroughExecutor("CodeCollector");

        var visionExec = new AgentExecutor(vision);
        var visionVerifierExec = new AgentExecutor(visionVerifier);
        var visionCollectorExec = new PassthroughExecutor("VisionCollector");

        var mergeExec = new MergeExecutor();
        var drafterExec = new AgentExecutor(drafter, isTerminal: true);

        var parallelExecs = new ExecutorBinding[] { kbExec, codeExec, visionExec };
        var collectorExecs = new ExecutorBinding[] { kbCollectorExec, codeCollectorExec, visionCollectorExec };

        var builder = new WorkflowBuilder(triageExec);
        builder.AddFanOutEdge(triageExec, parallelExecs);

        builder.AddEdge(kbExec, kbVerifierExec);
        builder.AddEdge<AgentContext>(kbVerifierExec, kbExec, ctx => ctx.KbVerification.Status == VerificationStatus.FailedRetrying);
        builder.AddEdge<AgentContext>(kbVerifierExec, kbCollectorExec, ctx => ctx.KbVerification.Status != VerificationStatus.FailedRetrying);

        builder.AddEdge(codeExec, codeVerifierExec);
        builder.AddEdge<AgentContext>(codeVerifierExec, codeExec, ctx => ctx.CodeVerification.Status == VerificationStatus.FailedRetrying);
        builder.AddEdge<AgentContext>(codeVerifierExec, codeCollectorExec, ctx => ctx.CodeVerification.Status != VerificationStatus.FailedRetrying);

        builder.AddEdge(visionExec, visionVerifierExec);
        builder.AddEdge<AgentContext>(visionVerifierExec, visionExec, ctx => ctx.VisionVerification.Status == VerificationStatus.FailedRetrying);
        builder.AddEdge<AgentContext>(visionVerifierExec, visionCollectorExec, ctx => ctx.VisionVerification.Status != VerificationStatus.FailedRetrying);

        builder.AddFanInBarrierEdge(collectorExecs, mergeExec);
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
