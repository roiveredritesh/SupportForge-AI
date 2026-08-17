namespace SupportForge.Core.Entities;

public sealed class Org
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ContactPerson { get; init; }
    public required string ContactNumber { get; init; }
    public required string Industry { get; init; }
    public string? Address { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // Sprint 3 (U13): connected MCP servers (GitHub today, more later) -- JSON-file-backed via
    // JsonFileOrgRepository, which round-trips this field for free (it serializes/deserializes the
    // whole Org object, no per-field wiring needed).
    public List<McpConnection> Connections { get; init; } = new();

    // Org-wide master switch for the code-graph Tier 2 LLM classification stage (sends up to 60
    // lines -- or 2KB for minified files -- of source content to the configured LLM provider). A
    // project's own CodeClassificationEnabled flag (Project.cs) also has to be true, so an admin
    // opts the org in AND each project separately opts in -- neither flag alone enables it. Default
    // false: classification never runs for a project until both consents are explicitly granted.
    public bool CodeClassificationEnabled { get; init; }
}
