using System.Collections.Concurrent;
using System.Threading;
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
