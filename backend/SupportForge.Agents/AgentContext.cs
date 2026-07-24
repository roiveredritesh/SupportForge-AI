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
    public List<(string Label, string Url)> Sources { get; } = new();
    public double Confidence { get; set; }
}
