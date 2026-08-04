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
/// isn't reliable without a real symbol table and is skipped here.
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

    public static CodeGraphFile Extract(string repoDir)
    {
        var graph = new CodeGraphFile();
        if (!Directory.Exists(repoDir)) return graph;

        var filesByRelativePath = EnumerateSourceFiles(repoDir)
            .ToDictionary(f => ToRelativePath(repoDir, f), f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var (relativePath, fullPath) in filesByRelativePath)
        {
            var fileType = FileTypeByExtension[Path.GetExtension(fullPath)];

            graph.Nodes.Add(new CodeGraphNode
            {
                Id = relativePath,
                Label = Path.GetFileName(fullPath),
                FileType = fileType,
                SourceFile = relativePath,
                SourceLocation = "L1",
            });

            string text;
            try { text = File.ReadAllText(fullPath); }
            catch (IOException) { continue; }

            if (DefinitionPatternByFileType.TryGetValue(fileType, out var definitionPattern))
                AddDefinitionNodesAndEdges(graph, relativePath, fileType, text, definitionPattern);

            AddImportEdges(graph, relativePath, fileType, text, filesByRelativePath.Keys);
        }

        return graph;
    }

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

    private static IEnumerable<string> EnumerateSourceFiles(string repoDir) =>
        EnumerateFiles(repoDir).Where(f => FileTypeByExtension.ContainsKey(Path.GetExtension(f)));

    private static IEnumerable<string> EnumerateFiles(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
            yield return file;

        foreach (var subDir in Directory.EnumerateDirectories(dir))
        {
            if (SkipDirNames.Contains(Path.GetFileName(subDir))) continue;
            foreach (var file in EnumerateFiles(subDir))
                yield return file;
        }
    }

    private static string ToRelativePath(string repoDir, string fullPath) =>
        Path.GetRelativePath(repoDir, fullPath).Replace('\\', '/');
}
