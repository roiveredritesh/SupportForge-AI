namespace SupportForge.Agents;

public sealed class AnthropicOptions
{
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string ChatModel { get; set; } = "claude-sonnet-4-20250514";
    public string? ApiKey { get; set; }
    public int MaxTokens { get; set; } = 4096;
}
