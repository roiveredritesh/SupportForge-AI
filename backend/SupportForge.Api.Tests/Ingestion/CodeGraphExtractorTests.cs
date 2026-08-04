using SupportForge.Ingestion.Code;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class CodeGraphExtractorTests
{
    private static string CreateRepoFixture(params (string RelativePath, string Content)[] files)
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        foreach (var (relativePath, content) in files)
        {
            var fullPath = Path.Combine(repoDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }
        return repoDir;
    }

    [Fact]
    public void Extract_CreatesFileNodeAndDefinitionNodes_ForCSharpClass()
    {
        var repoDir = CreateRepoFixture(("Auth.cs", "namespace Demo;\n\npublic sealed class Auth\n{\n    public void Login() { }\n}\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.Contains(graph.Nodes, n => n.Id == "Auth.cs" && n.FileType == "csharp");
        Assert.Contains(graph.Nodes, n => n.Id == "Auth.cs::Auth" && n.Label == "Auth");
        Assert.Contains(graph.Edges, e => e.Source == "Auth.cs" && e.Target == "Auth.cs::Auth" && e.Relation == "defines");
    }

    [Fact]
    public void Extract_ResolvesRelativeJsImport_ToTargetFileNode()
    {
        var repoDir = CreateRepoFixture(
            ("src/billing.ts", "export class Billing {}\n"),
            ("src/index.ts", "import { Billing } from './billing';\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.Contains(graph.Edges, e => e.Source == "src/index.ts" && e.Target == "src/billing.ts" && e.Relation == "imports");
    }

    [Fact]
    public void Extract_ResolvesDottedPythonImport_ToTargetFileNode()
    {
        var repoDir = CreateRepoFixture(
            ("pkg/mod.py", "def handler():\n    pass\n"),
            ("main.py", "from pkg.mod import handler\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.Contains(graph.Edges, e => e.Source == "main.py" && e.Target == "pkg/mod.py" && e.Relation == "imports");
    }

    [Fact]
    public void Extract_SkipsNonSourceFilesAndIgnoredDirectories()
    {
        var repoDir = CreateRepoFixture(
            ("README.md", "# hello"),
            ("node_modules/dep/index.js", "export class Ignored {}\n"),
            ("src/app.js", "export class App {}\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.DoesNotContain(graph.Nodes, n => n.SourceFile.Contains("README"));
        Assert.DoesNotContain(graph.Nodes, n => n.SourceFile.Contains("node_modules"));
        Assert.Contains(graph.Nodes, n => n.Id == "src/app.js");
    }

    [Fact]
    public void Extract_ReturnsEmptyGraph_WhenRepoDirDoesNotExist()
    {
        var graph = CodeGraphExtractor.Extract(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
    }
}
