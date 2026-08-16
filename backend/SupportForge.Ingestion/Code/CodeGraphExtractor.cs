using System.Text.RegularExpressions;
using SupportForge.Ingestion.Graph;

namespace SupportForge.Ingestion.Code;

/// <summary>
/// Structural (no LLM) code-graph extractor: walks a cloned repo and produces a <see cref="CodeGraphFile"/>
/// of file nodes, per-file type/function/class definition nodes, and best-effort import edges between
/// files -- replaces the semantic extraction the removed `graphify extract` CLI used to do. Regex-based
/// per-language heuristics, not a real parser: good enough to seed the code graph GraphImportJob loads
/// into Neo4j, not a substitute for a language server. Import edges are only resolved for languages whose
/// import syntax names a file path directly (relative JS/TS imports, dotted Python imports) -- C#/Java/Go
/// import/using statements name a namespace or package, not a file, so resolving those to a specific file
/// isn't reliable without a real symbol table and is skipped here. U23 adds a bounded second heuristic on
/// top of the same regex-only approach: an "endpoint" node per ASP.NET Core [HttpGet]/[HttpPost]/[Route]
/// action, and a "calls_endpoint" edge from any file whose HttpClient-shaped call names that verb+route --
/// this is what lets <c>GraphImportJob</c>/<c>BlastRadiusQueryTool</c> bridge two repos in one project.
/// </summary>
public static class CodeGraphExtractor
{
    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", ".venv", "venv", "__pycache__", ".next", "target",
    };

    private static readonly Dictionary<string, string> FileTypeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".ts"] = "typescript",
        [".tsx"] = "typescript",
        [".js"] = "javascript",
        [".jsx"] = "javascript",
        [".py"] = "python",
        [".java"] = "java",
        [".go"] = "go",
    };

    private static readonly Dictionary<string, Regex> DefinitionPatternByFileType = new()
    {
        ["csharp"] = new Regex(
            @"^\s*(?:\[[^\]]*\]\s*)*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|async|virtual|override)\s+)*\b(?:class|interface|struct|record|enum)\s+(?<name>[A-Za-z_]\w*)",
            RegexOptions.Compiled | RegexOptions.Multiline),
        ["typescript"] = new Regex(
            @"^\s*(?:export\s+)?(?:default\s+)?(?:abstract\s+)?\b(?:class|interface|function)\s+(?<name>[A-Za-z_]\w*)",
            RegexOptions.Compiled | RegexOptions.Multiline),
        ["javascript"] = new Regex(
            @"^\s*(?:export\s+)?(?:default\s+)?\b(?:class|function)\s+(?<name>[A-Za-z_]\w*)",
            RegexOptions.Compiled | RegexOptions.Multiline),
        ["python"] = new Regex(
            @"^\s*(?:class|def)\s+(?<name>[A-Za-z_]\w*)",
            RegexOptions.Compiled | RegexOptions.Multiline),
        ["java"] = new Regex(
            @"^\s*(?:(?:public|private|protected|static|final|abstract)\s+)*\b(?:class|interface|enum)\s+(?<name>[A-Za-z_]\w*)",
            RegexOptions.Compiled | RegexOptions.Multiline),
        ["go"] = new Regex(
            @"^\s*(?:func\s+(?:\([^)]*\)\s*)?(?<name>[A-Za-z_]\w*)\s*\(|type\s+(?<name>[A-Za-z_]\w*)\s+(?:struct|interface)\b)",
            RegexOptions.Compiled | RegexOptions.Multiline),
    };

    private static readonly Regex JsImportPattern = new(
        @"(?:import[^'""]*from\s*|require\()\s*['""](\.[^'""]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex PythonImportPattern = new(
        @"^\s*from\s+([\w.]+)\s+import\b",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex PythonModuleDocstring = new(
        "\\A\\s*[rRuU]?(\"\"\"|''')(?<body>.*?)\\1",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Tier 1 coverage ladder (U4): a language with both a definition pattern AND import resolution
    // gets "full" extraction; a definition pattern alone gets "partial" (definitions but no import
    // edges -- true today for csharp/java/go, whose import/using statements name a namespace or
    // package rather than a file path, so AddImportEdges never handles them); no definition pattern
    // gets "minimal" (file node only). This never blocks a file from getting *a* node -- R3.
    private static readonly HashSet<string> LanguagesWithImportResolution =
        new(StringComparer.Ordinal) { "javascript", "typescript", "python" };

    // Best-effort language fallback for a file whose extension isn't in FileTypeByExtension: a
    // shebang line names its interpreter directly, so a Python/Node script with no ".py"/".js"
    // extension still resolves to a real, indexable language instead of "unknown". Extend this table,
    // not FileTypeByExtension, when a new interpreter needs recognizing -- adding a language here does
    // not by itself grant it a definition pattern (that's still Full/Partial coverage above).
    private static readonly Dictionary<string, string> ShebangInterpreterToLanguage =
        new(StringComparer.OrdinalIgnoreCase) { ["python"] = "python", ["python3"] = "python", ["node"] = "javascript" };

    // U23: ASP.NET Core attribute-routing heuristic, bounded to this codebase's own controller style
    // (see ChatController.cs: [Route("api/chat")] class + [HttpPost("query")] action) -- not a
    // general ASP.NET Core route resolver (doesn't handle [ApiController] convention routing,
    // multiple [Route] overloads, or minimal-API MapGet/MapPost). Extend only when a real target
    // repo needs one of those.
    private static readonly Regex ControllerRoutePattern = new(
        @"\[Route\(\s*""(?<route>[^""]*)""\s*\)\]\s*(?:\[[^\]]*\]\s*)*(?:public\s+|internal\s+)?(?:partial\s+)?class",
        RegexOptions.Compiled);

    private static readonly Regex HttpMethodAttributePattern = new(
        @"\[Http(?<verb>Get|Post|Put|Delete|Patch)(?:\(\s*""(?<route>[^""]*)""\s*\))?\]",
        RegexOptions.Compiled);

    // Cross-repo endpoint-usage heuristic: an HttpClient call naming a verb+route, e.g.
    // httpClient.GetAsync("api/chat/query"). Deliberately loose (any *Async(verb) call with a
    // literal string argument) -- the extractor has no way to know at extraction time which repo (if
    // any) actually defines the target, so it always emits the edge; GraphImportJob's cross-repo
    // MATCH silently drops it if no matching endpoint node exists anywhere in the project.
    private static readonly Regex HttpClientCallPattern = new(
        @"\.(?<verb>Get|Post|Put|Delete|Patch)Async\s*\(\s*""(?<route>[^""]+)""",
        RegexOptions.Compiled);

    private const int MaxSummaryLength = 500;

    // Minified content has nothing meaningful for a regex definition-pattern to match, and a
    // multi-hundred-KB single-line file would dominate header-comment extraction cost for no benefit
    // -- cap what's read for summary purposes to the first 2KB (reused as-is by the Tier 2 enrichment
    // prompt input in a later phase of the same plan).
    private const int MinifiedSummaryInputCap = 2048;

    public static CodeGraphFile Extract(string repoDir)
    {
        var graph = new CodeGraphFile();
        if (!Directory.Exists(repoDir)) return graph;

        var filesByRelativePath = BuildRelativePathIndex(repoDir);

        foreach (var (relativePath, fullPath) in filesByRelativePath)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(fullPath); }
            catch (IOException) { continue; }

            var admissibility = CodeFileAdmissibility.Classify(Path.GetFileName(fullPath), bytes);
            if (!admissibility.IsAdmissible) continue; // Tier 0 reject: binary, undecodable, or empty

            string text;
            try { text = File.ReadAllText(fullPath); }
            catch (IOException) { continue; }

            var fileType = DetermineLanguage(fullPath, text);
            var isMinified = admissibility.Shape.HasFlag(CodeFileShape.Minified);
            var summaryInput = isMinified ? CapForMinifiedSummary(text) : text;

            graph.Nodes.Add(new CodeGraphNode
            {
                Id = relativePath,
                Label = Path.GetFileName(fullPath),
                FileType = fileType,
                SourceFile = relativePath,
                SourceLocation = "L1",
                Summary = ExtractFileHeaderComment(summaryInput, fileType),
                Shape = ShapeFlagsToNames(admissibility.Shape),
                CoverageLevel = DetermineCoverageLevel(fileType),
            });

            // Minified content isn't source a definition-pattern regex can meaningfully match against.
            if (!isMinified && DefinitionPatternByFileType.TryGetValue(fileType, out var definitionPattern))
                AddDefinitionNodesAndEdges(graph, relativePath, fileType, text, definitionPattern);

            AddImportEdges(graph, relativePath, fileType, text, filesByRelativePath.Keys);
            AddEndpointNodesAndEdges(graph, relativePath, fileType, text);
            AddEndpointUsageEdges(graph, relativePath, text);
        }

        return graph;
    }

    // Bug fix (E18): a case-only path collision (two real files differing only by case, a legitimate
    // state on a case-sensitive filesystem) used to throw ArgumentException out of ToDictionary and
    // crash extraction for the *entire* repo, not just those two files. Keep the first match and skip
    // the rest instead -- a rare naming collision degrades gracefully rather than aborting everything
    // (every other admissible file in the repo still gets a node).
    private static Dictionary<string, string> BuildRelativePathIndex(string repoDir)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fullPath in EnumerateSourceFiles(repoDir))
            index.TryAdd(ToRelativePath(repoDir, fullPath), fullPath);
        return index;
    }

    private static string CapForMinifiedSummary(string text) =>
        text.Length <= MinifiedSummaryInputCap ? text : text[..MinifiedSummaryInputCap];

    // R3: extension -> shebang -> "unknown", never a crash and never "no node at all". A recognized
    // extension always wins even if a shebang is also present (an extension is a stronger signal than
    // a first-line guess).
    private static string DetermineLanguage(string fullPath, string text) =>
        FileTypeByExtension.TryGetValue(Path.GetExtension(fullPath), out var byExtension)
            ? byExtension
            : DetectShebangLanguage(text) ?? "unknown";

    private static string? DetectShebangLanguage(string text)
    {
        var firstLine = text.Split('\n', 2)[0].TrimEnd('\r');
        if (!firstLine.StartsWith("#!", StringComparison.Ordinal)) return null;

        var parts = firstLine[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        // "#!/usr/bin/env python3" names the real interpreter in the second token; a direct
        // "#!/usr/bin/python3" names it as the last path segment of the first token.
        var interpreterToken = Path.GetFileName(parts[0]).Equals("env", StringComparison.OrdinalIgnoreCase) && parts.Length > 1
            ? parts[1]
            : Path.GetFileName(parts[0]);

        return ShebangInterpreterToLanguage.GetValueOrDefault(interpreterToken);
    }

    private static string DetermineCoverageLevel(string fileType)
    {
        if (!DefinitionPatternByFileType.ContainsKey(fileType)) return "minimal";
        return LanguagesWithImportResolution.Contains(fileType) ? "full" : "partial";
    }

    private static List<string> ShapeFlagsToNames(CodeFileShape shape) =>
        Enum.GetValues<CodeFileShape>()
            .Where(v => v != CodeFileShape.None && shape.HasFlag(v))
            .Select(v => v.ToString())
            .ToList();

    // U23: endpoint node type -- one per [HttpGet]/[HttpPost]/etc action found in a csharp file,
    // combining the controller's [Route] prefix (if any) with the action's own route template.
    private static void AddEndpointNodesAndEdges(CodeGraphFile graph, string relativePath, string fileType, string text)
    {
        if (fileType != "csharp") return;

        var routePrefixMatch = ControllerRoutePattern.Match(text);
        var routePrefix = routePrefixMatch.Success ? routePrefixMatch.Groups["route"].Value : null;

        foreach (Match match in HttpMethodAttributePattern.Matches(text))
        {
            var verb = match.Groups["verb"].Value.ToUpperInvariant();
            var methodRoute = match.Groups["route"].Success ? match.Groups["route"].Value : null;
            var route = CombineRoute(routePrefix, methodRoute);
            if (route.Length == 0) continue; // no route info at all (e.g. bare [HttpGet] with no controller-level [Route])

            var endpointId = EndpointNodeId(verb, route);
            if (graph.Nodes.Any(n => n.Id == endpointId)) continue; // same endpoint attribute matched twice

            graph.Nodes.Add(new CodeGraphNode
            {
                Id = endpointId,
                Label = $"{verb} {route}",
                FileType = fileType,
                SourceFile = relativePath,
                SourceLocation = $"L{CountLinesBefore(text, match.Index)}",
                Summary = "",
            });
            graph.Edges.Add(new CodeGraphEdge { Source = relativePath, Target = endpointId, Relation = "defines_endpoint", Confidence = "high" });
        }
    }

    // U23: calls_endpoint edge type -- emitted from every file's HttpClient-shaped calls,
    // regardless of whether the target endpoint is known to exist (see HttpClientCallPattern comment).
    private static void AddEndpointUsageEdges(CodeGraphFile graph, string relativePath, string text)
    {
        foreach (Match match in HttpClientCallPattern.Matches(text))
        {
            var verb = match.Groups["verb"].Value.ToUpperInvariant();
            var route = match.Groups["route"].Value.Trim('/');
            if (route.Length == 0) continue;

            graph.Edges.Add(new CodeGraphEdge { Source = relativePath, Target = EndpointNodeId(verb, route), Relation = "calls_endpoint", Confidence = "medium" });
        }
    }

    private static string CombineRoute(string? prefix, string? methodRoute)
    {
        var parts = new[] { prefix, methodRoute }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim('/'));
        return string.Join('/', parts);
    }

    private static string EndpointNodeId(string verb, string route) => $"endpoint::{verb} {route}";

    private static void AddDefinitionNodesAndEdges(
        CodeGraphFile graph, string relativePath, string fileType, string text, Regex definitionPattern)
    {
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in definitionPattern.Matches(text))
        {
            var name = match.Groups["name"].Value;
            if (!seenNames.Add(name)) continue; // same symbol matched by more than one alternation branch

            var nodeId = $"{relativePath}::{name}";
            graph.Nodes.Add(new CodeGraphNode
            {
                Id = nodeId,
                Label = name,
                FileType = fileType,
                SourceFile = relativePath,
                SourceLocation = $"L{CountLinesBefore(text, match.Index)}",
                Summary = ExtractPrecedingCommentBlock(text, match.Index),
            });
            graph.Edges.Add(new CodeGraphEdge { Source = relativePath, Target = nodeId, Relation = "defines", Confidence = "high" });
        }
    }

    private static void AddImportEdges(
        CodeGraphFile graph, string relativePath, string fileType, string text, IEnumerable<string> knownRelativePaths)
    {
        if (fileType is "javascript" or "typescript")
        {
            var fromDir = Path.GetDirectoryName(relativePath) ?? "";
            foreach (Match match in JsImportPattern.Matches(text))
            {
                var target = ResolveRelativeImport(fromDir, match.Groups[1].Value, knownRelativePaths);
                if (target is not null)
                    graph.Edges.Add(new CodeGraphEdge { Source = relativePath, Target = target, Relation = "imports", Confidence = "medium" });
            }
        }
        else if (fileType == "python")
        {
            foreach (Match match in PythonImportPattern.Matches(text))
            {
                var modulePath = match.Groups[1].Value.Replace('.', '/') + ".py";
                var target = knownRelativePaths.FirstOrDefault(p => p.Replace('\\', '/').EndsWith(modulePath, StringComparison.OrdinalIgnoreCase));
                if (target is not null)
                    graph.Edges.Add(new CodeGraphEdge { Source = relativePath, Target = target, Relation = "imports", Confidence = "medium" });
            }
        }
    }

    // Manual '/'-segment resolution (not Path.GetFullPath) -- import specifiers are always POSIX-style
    // regardless of host OS, and anchoring them at a filesystem root would pull in platform-specific
    // drive/UNC handling that has nothing to do with resolving a relative module path.
    private static string? ResolveRelativeImport(string fromDir, string importSpecifier, IEnumerable<string> knownRelativePaths)
    {
        var segments = new List<string>(fromDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var segment in importSpecifier.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }
            segments.Add(segment);
        }
        var combined = string.Join('/', segments);

        return knownRelativePaths.FirstOrDefault(p =>
        {
            var normalized = p.Replace('\\', '/');
            var withoutExtension = Path.ChangeExtension(normalized, null);
            return string.Equals(normalized, combined, StringComparison.OrdinalIgnoreCase)
                || string.Equals(withoutExtension, combined, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static int CountLinesBefore(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static bool IsCommentLine(string trimmed) =>
        trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*");

    private static string StripCommentMarkers(string trimmed) =>
        trimmed.TrimStart('/', '*').TrimEnd('*', '/').Trim();

    private static string Truncate(string s) =>
        s.Length <= MaxSummaryLength ? s : s[..MaxSummaryLength] + "…";

    // This codebase's dominant place for "why this file exists" prose is a block comment at the very
    // top (see e.g. staging-guard.ts, cdp-allowlist.ts) -- identifier names and edges tell an agent
    // WHAT exists, this is the one place a regex-only pass can recover WHY. Best-effort: a shebang
    // line, missing header, or non-comment first line just yields an empty summary.
    private static string ExtractFileHeaderComment(string text, string fileType)
    {
        if (fileType == "python") return ExtractPythonModuleDocstring(text);

        var lines = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var trimmed = rawLine.TrimEnd('\r').Trim();
            if (trimmed.Length == 0)
            {
                if (lines.Count > 0) break; // blank line after content ends the header
                continue; // allow blank lines before the header starts
            }
            if (!IsCommentLine(trimmed)) break;
            var stripped = StripCommentMarkers(trimmed);
            if (stripped.Length > 0) lines.Add(stripped);
        }
        return Truncate(string.Join(' ', lines));
    }

    private static string ExtractPythonModuleDocstring(string text)
    {
        var match = PythonModuleDocstring.Match(text);
        return match.Success ? Truncate(match.Groups["body"].Value.Trim()) : "";
    }

    // Backward from a definition's match start: XML-doc (///), JSDoc (/** */), and plain // blocks
    // directly above it, the dominant per-symbol doc-comment convention for C#/TS/JS/Java/Go. Stops
    // at the first blank or non-comment line -- a real doc comment sits with no gap above its symbol.
    // Not attempted for Python: its docstring is the first statement INSIDE the body, not a comment
    // above it, and a regex pass can't reliably find where a (possibly multi-line) signature ends.
    private static string ExtractPrecedingCommentBlock(string text, int definitionIndex)
    {
        if (definitionIndex <= 0) return "";

        var lines = new List<string>();
        var lineEnd = text.LastIndexOf('\n', definitionIndex - 1);
        while (lineEnd >= 0)
        {
            var lineStart = text.LastIndexOf('\n', lineEnd - 1) + 1;
            var line = text[lineStart..lineEnd].TrimEnd('\r').Trim();
            if (line.Length == 0 || !IsCommentLine(line)) break;
            var stripped = StripCommentMarkers(line);
            if (stripped.Length > 0) lines.Insert(0, stripped);
            lineEnd = lineStart - 1;
        }
        return Truncate(string.Join(' ', lines));
    }

    // U4: no longer filtered by extension -- every file reaches CodeFileAdmissibility in Extract(),
    // which is the actual admission gate now (R3: any admissible file gets at least a file node,
    // regardless of language).
    private static IEnumerable<string> EnumerateSourceFiles(string repoDir) =>
        EnumerateFiles(repoDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    // E17: a symlinked subdirectory is resolved and tracked before recursing into it, so a circular
    // symlink structure (a link pointing back at an already-visited real directory) terminates instead
    // of recursing forever. Ordinary (non-symlinked) directories can't cycle -- the filesystem is a
    // tree -- so only symlinked entries pay this check.
    private static IEnumerable<string> EnumerateFiles(string dir, HashSet<string> visitedRealDirs)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
            yield return file;

        foreach (var subDir in Directory.EnumerateDirectories(dir))
        {
            if (SkipDirNames.Contains(Path.GetFileName(subDir))) continue;

            var info = new DirectoryInfo(subDir);
            if (info.LinkTarget is not null)
            {
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? subDir;
                if (!visitedRealDirs.Add(resolved)) continue; // already visited -- cycle, skip
            }

            foreach (var file in EnumerateFiles(subDir, visitedRealDirs))
                yield return file;
        }
    }

    private static string ToRelativePath(string repoDir, string fullPath) =>
        Path.GetRelativePath(repoDir, fullPath).Replace('\\', '/');
}
