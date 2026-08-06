namespace SupportForge.Agents;

public sealed class AnthropicOptions
{
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string ChatModel { get; set; } = "claude-sonnet-4-20250514";
    // Model tiering (gap-closing-solutions.md Phase C, item 3): cheap-tier classifier/judge calls
    // (Triage, verifiers) use this model instead of ChatModel when set; falls back to ChatModel
    // (i.e. no tiering) when unset, so this is opt-in per deployment, not a forced behavior change.
    public string? CheapChatModel { get; set; }
    public string? ApiKey { get; set; }
    public int MaxTokens { get; set; } = 4096;
}
