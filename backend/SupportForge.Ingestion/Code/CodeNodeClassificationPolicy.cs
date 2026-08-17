namespace SupportForge.Ingestion.Code;

// U8 of the code-graph classification plan (docs/plans/2026-08-16-001-feat-code-graph-classification-plan.md):
// a pure, local, no-network mapping from a CodeNodeClassifier observation to an include/exclude
// decision. KTD5: the LLM (CodeNodeClassifier) never decides inclusion itself -- it only reports
// what a file *is*. Keeping that decision here means retuning it (raise a threshold, stop excluding
// generated code) is a code/config change with zero LLM cost and zero cache invalidation, instead of
// a prompt change that would force reclassifying every file in every project.
public static class CodeNodeClassificationPolicy
{
    // R6/KTD6 (fail-open): any kind not explicitly listed below -- including "" (unclassified,
    // consent off or classification failed) and any value a future prompt version might introduce --
    // defaults to included. Only "vendored" and "generated" can ever exclude, and only above their
    // confidence threshold.
    private const double VendoredExclusionThreshold = 0.8;
    private const double GeneratedExclusionThreshold = 0.8;

    // OQ2 resolved: config/data-shaped content (bulk data, localization, migrations, barrel/re-export
    // files) is not special-cased -- it is classified and policed exactly like any other kind, i.e.
    // it falls through to the default-include case below like "handwritten"/"test"/"unknown" already
    // do. There is deliberately no forced-include carve-out for kind == "config" here.
    public static bool Include(string kind, double confidence, IReadOnlyCollection<string> shapeMarks)
    {
        return kind switch
        {
            // A Tier 0 Generated shape mark must also agree, per the design's "generated code can be
            // domain-rich" caveat (EF migrations, protobuf/OpenAPI clients) -- LLM confidence alone
            // is not sufficient to exclude.
            "generated" => confidence < GeneratedExclusionThreshold || !shapeMarks.Contains("Generated"),
            "vendored" => confidence < VendoredExclusionThreshold,
            _ => true,
        };
    }
}
