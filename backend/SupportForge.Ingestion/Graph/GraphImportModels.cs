using System.Text.Json.Serialization;

namespace SupportForge.Ingestion.Graph;

// Shape a repo's extracted code graph is expected in on disk before GraphImportJob loads it into
// Neo4j: { nodes: [...], edges: [...] }.
public sealed class CodeGraphFile
{
    [JsonPropertyName("nodes")]
    public List<CodeGraphNode> Nodes { get; set; } = [];

    [JsonPropertyName("edges")]
    public List<CodeGraphEdge> Edges { get; set; } = [];
}

public sealed class CodeGraphNode
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

    // Leading doc-comment/header-comment prose captured verbatim (best-effort, regex-based -- see
    // CodeGraphExtractor). Structural fields above tell an agent WHAT exists; this is the one place
    // this extractor recovers WHY, since it never parses a real AST and has no other way to know.
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    // Tier 0 admissibility marks (CodeFileAdmissibility) as flag names, e.g. ["Minified", "Banner"].
    // Never an exclusion decision on its own -- consumed by later classification-pipeline stages
    // (docs/plans/2026-08-16-001-feat-code-graph-classification-plan.md). Empty for a plain
    // hand-written file with no shape marks.
    [JsonPropertyName("shape")]
    public List<string> Shape { get; set; } = [];

    // Tier 1 coverage ladder: "full" (definitions + import edges), "partial" (definitions only), or
    // "minimal" (file node only -- no definition pattern registered for this FileType). Always set on
    // a file node; "" on a definition/endpoint node, which doesn't have its own coverage level.
    [JsonPropertyName("coverage_level")]
    public string CoverageLevel { get; set; } = "";

    // Tier 2 (CodeNodeClassifier) observations -- never an inclusion decision by themselves (that's
    // CodeNodeClassificationPolicy's job). "" / empty / 0 means unclassified: either the org/project
    // consent gate is off, or classification failed and fell back per R6 (fail-open, never cached).
    // KTD4: classification happens at file level, and Kind/Confidence/Layer are copied onto each of
    // the file's definition nodes too -- GraphImportJob's hard-delete filter (U9) operates per node,
    // so a definition node needs its own copy of the verdict to cascade its file's exclusion/inclusion
    // correctly. Purpose is derived independently per definition (same LLM call, richer signal than
    // the file's own Purpose). DomainTerms stays file-only -- supplementary retrieval metadata, not
    // needed for policy, and not worth deriving per-definition.
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = "";

    [JsonPropertyName("domain_terms")]
    public List<string> DomainTerms { get; set; } = [];

    [JsonPropertyName("layer")]
    public string Layer { get; set; } = "";
}

public sealed class CodeGraphEdge
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
