using System.Collections.Concurrent;
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

    public string Intent { get; set; } = string.Empty;
    public FreshnessContext? Freshness { get; set; }
    // WS4 (retrieval-pipeline remediation plan): written by CrossReferenceAgent from KB findings,
    // folded into CodeAnalyzerAgent's graphify query text.
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
    public int TotalTokensUsed { get; set; }
    public VerificationResult KbVerification { get; } = new();
    public VerificationResult CodeVerification { get; } = new();
    public VerificationResult VisionVerification { get; } = new();
}
