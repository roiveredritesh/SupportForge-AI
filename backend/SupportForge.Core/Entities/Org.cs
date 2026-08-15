namespace SupportForge.Core.Entities;

public sealed class Org
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Sprint 3 (U13): connected MCP servers (GitHub today, more later) -- JSON-file-backed via
    // JsonFileOrgRepository, which round-trips this field for free (it serializes/deserializes the
    // whole Org object, no per-field wiring needed).
    public List<McpConnection> Connections { get; init; } = new();
}
