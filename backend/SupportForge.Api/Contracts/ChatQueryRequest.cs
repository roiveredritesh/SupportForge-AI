namespace SupportForge.Api.Contracts;

public sealed class ChatQueryRequest
{
    public required string ProjectId { get; init; }
    public required string Query { get; init; }
    public string? ScreenshotBase64 { get; init; }
}
