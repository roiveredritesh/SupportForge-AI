namespace SupportForge.Agents;

public sealed class BedrockOptions
{
    public string Region { get; set; } = "us-east-1";
    public string ChatModel { get; set; } = "anthropic.claude-3-5-sonnet-20241022-v2:0";
    public string EmbeddingModel { get; set; } = "amazon.titan-embed-text-v2:0";
    public int MaxTokens { get; set; } = 4096;
}
