namespace SupportForge.Ingestion.Graph;

/// <summary>
/// Connection settings for the Neo4j-backed code graph. Neo4j.Driver speaks the Bolt protocol, which
/// Memgraph also implements, so the same driver/options serve either backend -- only
/// <see cref="Uri"/> changes.
/// </summary>
public sealed class Neo4jOptions
{
    public string Uri { get; set; } = "bolt://localhost:7687";
    public string User { get; set; } = "neo4j";
    public string Password { get; set; } = "";
    public string Database { get; set; } = "neo4j";

    // Neo4j.Driver pools connections internally per IDriver instance. 0 = driver default (100).
    public int MaxConnectionPoolSize { get; set; }
}
