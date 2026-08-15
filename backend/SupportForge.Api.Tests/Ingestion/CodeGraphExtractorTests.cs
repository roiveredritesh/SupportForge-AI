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

    [Fact]
    public void Extract_CapturesFileHeaderBlockComment_AsFileNodeSummary()
    {
        var repoDir = CreateRepoFixture(("cdp-allowlist.ts",
            "/**\n" +
            " * CDP method allow-list (T2: deny-default).\n" +
            " *\n" +
            " * Missing a method means it's blocked, not exposed.\n" +
            " */\n\n" +
            "export type CdpScope = 'tab' | 'browser';\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var fileNode = Assert.Single(graph.Nodes, n => n.Id == "cdp-allowlist.ts");
        Assert.Contains("CDP method allow-list (T2: deny-default).", fileNode.Summary);
        Assert.Contains("Missing a method means it's blocked, not exposed.", fileNode.Summary);
    }

    [Fact]
    public void Extract_CapturesFileHeaderLineComments_AsFileNodeSummary()
    {
        var repoDir = CreateRepoFixture(("Auth.cs",
            "// AuthService validates session tokens against the identity provider.\n" +
            "// It never talks to the database directly.\n" +
            "namespace Demo;\n\npublic sealed class Auth { }\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var fileNode = Assert.Single(graph.Nodes, n => n.Id == "Auth.cs");
        Assert.Contains("AuthService validates session tokens", fileNode.Summary);
        Assert.Contains("never talks to the database directly", fileNode.Summary);
    }

    [Fact]
    public void Extract_CapturesPythonModuleDocstring_AsFileNodeSummary()
    {
        var repoDir = CreateRepoFixture(("mod.py",
            "\"\"\"Handles inbound webhook signature verification.\"\"\"\n\ndef handler():\n    pass\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var fileNode = Assert.Single(graph.Nodes, n => n.Id == "mod.py");
        Assert.Contains("Handles inbound webhook signature verification.", fileNode.Summary);
    }

    [Fact]
    public void Extract_CapturesDocCommentImmediatelyAboveDefinition_AsDefinitionNodeSummary()
    {
        var repoDir = CreateRepoFixture(("Auth.cs",
            "namespace Demo;\n\n" +
            "// Validates a session token and returns the associated user id, or null if expired.\n" +
            "public sealed class Auth\n{\n    public void Login() { }\n}\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var classNode = Assert.Single(graph.Nodes, n => n.Id == "Auth.cs::Auth");
        Assert.Contains("Validates a session token", classNode.Summary);
    }

    [Fact]
    public void Extract_DoesNotAttachSummary_WhenNoCommentPrecedesDefinition()
    {
        var repoDir = CreateRepoFixture(("Auth.cs", "namespace Demo;\n\npublic sealed class Auth { }\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var classNode = Assert.Single(graph.Nodes, n => n.Id == "Auth.cs::Auth");
        Assert.Equal("", classNode.Summary);
    }

    [Fact]
    public void Extract_TruncatesLongSummary()
    {
        var longComment = string.Join("\n", Enumerable.Range(0, 100).Select(i => $"// line {i} of a very long header comment block that keeps going"));
        var repoDir = CreateRepoFixture(("big.ts", longComment + "\n\nexport class Big {}\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var fileNode = Assert.Single(graph.Nodes, n => n.Id == "big.ts");
        Assert.True(fileNode.Summary.Length <= 501); // 500 chars + the truncation ellipsis
        Assert.EndsWith("…", fileNode.Summary);
    }

    [Fact]
    public void Extract_CreatesEndpointNode_ForControllerActionWithRouteAttributes()
    {
        var repoDir = CreateRepoFixture(("ChatController.cs",
            "namespace Demo;\n\n" +
            "[Route(\"api/chat\")]\n" +
            "public partial class ChatController\n{\n" +
            "    [HttpPost(\"query\")]\n" +
            "    public void Query() { }\n" +
            "}\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var endpoint = Assert.Single(graph.Nodes, n => n.Id == "endpoint::POST api/chat/query");
        Assert.Equal("POST api/chat/query", endpoint.Label);
        Assert.Contains(graph.Edges, e => e.Source == "ChatController.cs" && e.Target == "endpoint::POST api/chat/query" && e.Relation == "defines_endpoint");
    }

    [Fact]
    public void Extract_CreatesCallsEndpointEdge_ForHttpClientCallMatchingAnEndpointRoute()
    {
        var repoDir = CreateRepoFixture(("client/Widget.cs",
            "namespace OtherRepo;\n\n" +
            "public class WidgetClient\n{\n" +
            "    public async Task Fetch(HttpClient http) => await http.GetAsync(\"api/chat/query\");\n" +
            "}\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.Contains(graph.Edges, e => e.Source == "client/Widget.cs" && e.Target == "endpoint::GET api/chat/query" && e.Relation == "calls_endpoint");
    }

    [Fact]
    public void Extract_CreatesNoEndpointNode_ForFileWithNoHttpAttributes()
    {
        var repoDir = CreateRepoFixture(("Plain.cs", "namespace Demo;\n\npublic class Plain\n{\n    public void DoWork() { }\n}\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.DoesNotContain(graph.Nodes, n => n.Id.StartsWith("endpoint::", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Edges, e => e.Relation is "defines_endpoint" or "calls_endpoint");
    }
}
