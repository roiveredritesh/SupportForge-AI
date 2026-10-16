using System.Collections.Concurrent;
using System.Threading;
using SupportForge.Agents.Triage;
using SupportForge.Core;

namespace SupportForge.Agents;

// WS3 (retrieval-pipeline remediation plan): pre-query staleness signal, written by
// FreshnessGateAgent before the KB/Code/Vision fan-out, consumed by DrafterAgent.
public sealed record FreshnessContext(bool SyncInProgress, FreshnessScore Score);

public sealed class AgentContext
{
    public required string ProjectId { get; init; }
    public required string Query { get; init; }
    public string? ScreenshotBase64 { get; init; }
    // U10: customer-supplied product version and free-form config (key-value), threaded from the
    // chat request through to KbResearcherAgent's retrieval bias and DrafterAgent's version-disclaimer
    // instruction. Both optional -- most callers won't set either.
    public string? ProductVersion { get; init; }
    public IReadOnlyDictionary<string, string>? Config { get; init; }

    public string Intent { get; set; } = string.Empty;
    // U2 (JEV intent classification plan): richer intent output written by TriageAgent from the classifier's
    // IntentDecision. Scores and signals stay empty until a classifier fills them; the LLM classifier reports 1.0.
    public double? IntentConfidence { get; set; }
    public IReadOnlyDictionary<string, double> IntentScores { get; set; } = new Dictionary<string, double>();
    public IReadOnlyDictionary<string, double> IntentSignals { get; set; } = new Dictionary<string, double>();
    public string IntentSource { get; set; } = string.Empty;
    // U6 (JEV intent classification plan): the TriageDecisionPolicy rule that chose Intent, for example "top",
    // "greeting-has-question" or "low-confidence". Set by TriageAgent and logged on the Triage span (triage.rule).
    public string IntentRule { get; set; } = string.Empty;
    // U8 (JEV intent classification plan): "high" or "medium" for a Jev-decided, non-unclear intent, otherwise null.
    // It is null whenever the acting classifier is the LLM ("llm" or "llm-fallback"), so LLM-mode prompts stay unchanged.
    public string? IntentConfidenceBand { get; set; }
    // U7 secondary-intent routing inputs. Off by default; nothing turns them on until the Jev classifier lands.
    public bool SecondaryIntentEnabled { get; set; }
    public double SecondaryMinProbability { get; set; } = 0.35;
    // Highest-scoring label in IntentScores other than Intent, with a score > 0. Ties broken by TriageLabels.All order
    // (labels not in TriageLabels.All sort after them, ordinal). Null when there is none.
    public (string Label, double Probability)? RunnerUpIntent
    {
        get
        {
            (string Label, double Probability)? best = null;
            foreach (var (label, probability) in IntentScores)
            {
                // `!(x > 0)` rather than `x <= 0` so a NaN score is skipped too.
                if (label == Intent || !(probability > 0))
                {
                    continue;
                }

                if (best is null || probability > best.Value.Probability
                    || (probability == best.Value.Probability && TriageLabels.CompareTieBreak(label, best.Value.Label) < 0))
                {
                    best = (label, probability);
                }
            }

            return best;
        }
    }

    public FreshnessContext? Freshness { get; set; }
    // WS4 (retrieval-pipeline remediation plan): written by CrossReferenceAgent from KB findings,
    // folded into CodeAnalyzerAgent's code graph query text.
    public string? CodeQueryAugmentation { get; set; }
    public List<(string Role, string Content)> History { get; } = new();
    public List<string> KbSnippets { get; } = new();
    public List<string> CodeSnippets { get; } = new();
    public string? VisionFindings { get; set; }
    public string Draft { get; set; } = string.Empty;

    // ConcurrentBag: KbResearcherAgent and CodeAnalyzerAgent both write here from
    // concurrent fan-out branches in CoordinatorPipeline.
    public ConcurrentBag<(string Label, string Url)> Sources { get; } = new();
    public double Confidence { get; set; }
    public int TotalTokensUsed => _totalTokensUsed;
    public VerificationResult KbVerification { get; } = new();
    public VerificationResult CodeVerification { get; } = new();
    public VerificationResult VisionVerification { get; } = new();

    // ConcurrentDictionary: written from concurrent fan-out branches (same as Sources above).
    public ConcurrentDictionary<string, int> TokensByAgent { get; } = new();

    public void AddTokens(string agent, int tokens)
    {
        TokensByAgent.AddOrUpdate(agent, tokens, (_, existing) => existing + tokens);
        Interlocked.Add(ref _totalTokensUsed, tokens);
    }

    private int _totalTokensUsed;
}
