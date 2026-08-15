namespace SupportForge.Api.Contracts;

public sealed class ChatQueryRequest
{
    public required string ProjectId { get; init; }
    public required string Query { get; init; }
    public string? ScreenshotBase64 { get; init; }
    public string? ConversationId { get; init; }
    // U10: optional customer-supplied product version + free-form config, biasing KB retrieval and
    // driving DrafterAgent's version-disclaimer instruction. Free-text, not a dropdown-backed enum --
    // there's no existing version registry in this codebase to validate against.
    public string? ProductVersion { get; init; }
    public IReadOnlyDictionary<string, string>? Config { get; init; }
}
