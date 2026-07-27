using System.Collections.Concurrent;

namespace SupportForge.Agents;

public sealed class AgentContext
{
    public required string ProjectId { get; init; }
    public required string Query { get; init; }
    public string? ScreenshotBase64 { get; init; }

    public string Intent { get; set; } = string.Empty;
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
