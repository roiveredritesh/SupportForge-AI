using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SupportForge.Agents.Triage;

namespace SupportForge.Agents;

// U2 (JEV intent classification plan): TriageAgent no longer calls the LLM itself. Classification sits behind
// IIntentClassifier; this agent maps the classifier's scores onto the label set and records the intent fields on
// AgentContext. Label behaviour is unchanged while the classifier is LlmIntentClassifier.
public sealed class TriageAgent : IAgent
{
    private readonly IIntentClassifier _classifier;
    private readonly ILogger<TriageAgent> _logger;
    public string Name => "Triage";

    public TriageAgent(IIntentClassifier classifier, ILogger<TriageAgent> logger)
    {
        _classifier = classifier;
        _logger = logger;
    }

    // Convenience constructor kept so every existing call site keeps compiling unchanged.
    public TriageAgent(ILlmChatClient llm, ILogger<TriageAgent> logger) : this(new LlmIntentClassifier(llm), logger) { }

    public async Task<AgentContext> RunAsync(AgentContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("{Agent} starting: project={ProjectId} queryLength={QueryLength}", Name, context.ProjectId, context.Query.Length);
        try
        {
            var decision = await _classifier.ClassifyAsync(context, TriageLabels.All, ct);
            var top = TopLabel(decision.Scores);
            // Anything we can't map is treated as "unclear" so the downstream gates no-op and the
            // Drafter asks for clarification, rather than answering from empty retrieval context.
            context.Intent = TriageLabels.IsKnown(top) ? top : TriageLabels.Unclear;
            context.IntentConfidence = decision.Confidence;
            context.IntentScores = decision.Scores;
            context.IntentSignals = decision.Signals;
            context.IntentSource = decision.Source;
            context.AddTokens(Name, decision.TokensUsed);
            _logger.LogInformation("{Agent} completed in {ElapsedMs}ms: intent={Intent}", Name, sw.ElapsedMilliseconds, context.Intent);
            return context;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Agent} failed after {ElapsedMs}ms", Name, sw.ElapsedMilliseconds);
            throw;
        }
    }

    // Highest-scoring label; ties go to TriageLabels.All order. Empty (or all-NaN) scores return "unclear".
    // A top label outside TriageLabels is returned as-is and mapped to "unclear" by the caller.
    private static string TopLabel(IReadOnlyDictionary<string, double> scores)
    {
        string? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var (label, score) in scores)
        {
            if (double.IsNaN(score))
            {
                continue;
            }

            if (best is null || score > bestScore || (score == bestScore && TriageLabels.CompareTieBreak(label, best) < 0))
            {
                best = label;
                bestScore = score;
            }
        }

        return best ?? TriageLabels.Unclear;
    }
}
