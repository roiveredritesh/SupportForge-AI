using System.Text.Json.Serialization;

namespace SupportForge.Ingestion.Graphify;

// Mirrors the exact shape `graphify extract --no-cluster` writes to graphify-out/graph.json
// (verified by running graphify extract directly): { nodes: [...], edges: [...], hyperedges: [...],
// input_tokens, output_tokens }. hyperedges/token counts are ignored here -- WS1 only imports the
// plain node/edge graph that GraphDbQueryTool traverses.
public sealed class GraphifyGraphFile
{
    [JsonPropertyName("nodes")]
    public List<GraphifyNode> Nodes { get; set; } = [];

    [JsonPropertyName("edges")]
    public List<GraphifyEdge> Edges { get; set; } = [];
}

public sealed class GraphifyNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("file_type")]
    public string FileType { get; set; } = "";

    [JsonPropertyName("source_file")]
    public string SourceFile { get; set; } = "";

    [JsonPropertyName("source_location")]
    public string SourceLocation { get; set; } = "";
}

public sealed class GraphifyEdge
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("relation")]
    public string Relation { get; set; } = "";

    [JsonPropertyName("confidence")]
    public string Confidence { get; set; } = "";
}
