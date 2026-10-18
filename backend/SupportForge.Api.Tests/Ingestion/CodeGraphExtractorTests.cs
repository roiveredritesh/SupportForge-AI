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

    // U4 (R3): a recognized extension still gets full extraction, and an ignored directory
    // (node_modules) is still skipped entirely. A file with no recognized extension (README.md) is no
    // longer skipped -- it now gets a minimal-level node with language "unknown" (see the next test),
    // which is exactly the coverage-ladder behavior this unit adds.
    [Fact]
    public void Extract_SkipsIgnoredDirectories_ButNotUnrecognizedExtensions()
    {
        var repoDir = CreateRepoFixture(
            ("README.md", "# hello"),
            ("node_modules/dep/index.js", "export class Ignored {}\n"),
            ("src/app.js", "export class App {}\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.DoesNotContain(graph.Nodes, n => n.SourceFile.Contains("node_modules"));
        Assert.Contains(graph.Nodes, n => n.Id == "src/app.js");
    }

    [Fact]
    public void Extract_AdmitsUnrecognizedExtension_AsMinimalLevelUnknownLanguageNode()
    {
        var repoDir = CreateRepoFixture(("README.md", "# hello\n\nSome project documentation.\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var node = Assert.Single(graph.Nodes, n => n.Id == "README.md");
        Assert.Equal("unknown", node.FileType);
        Assert.Equal("minimal", node.CoverageLevel);
    }

    [Fact]
    public void Extract_DetectsPythonFromShebang_ForExtensionlessScript()
    {
        var repoDir = CreateRepoFixture(("run", "#!/usr/bin/env python3\ndef main():\n    pass\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var fileNode = Assert.Single(graph.Nodes, n => n.Id == "run");
        Assert.Equal("python", fileNode.FileType);
        Assert.Contains(graph.Nodes, n => n.Id == "run::main");
    }

    [Fact]
    public void Extract_RecognizedExtension_GetsFullCoverageLevel()
    {
        var repoDir = CreateRepoFixture(("src/app.js", "export class App {}\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var fileNode = Assert.Single(graph.Nodes, n => n.Id == "src/app.js");
        Assert.Equal("full", fileNode.CoverageLevel);
    }

    [Fact]
    public void Extract_DefinitionPatternWithNoImportResolution_GetsPartialCoverageLevel()
    {
        var repoDir = CreateRepoFixture(("Auth.cs", "namespace Demo;\npublic class Auth {}\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var fileNode = Assert.Single(graph.Nodes, n => n.Id == "Auth.cs");
        Assert.Equal("partial", fileNode.CoverageLevel);
    }

    [Fact]
    public void Extract_DoesNotHang_OnCircularSymlinkDirectory()
    {
        var repoDir = CreateRepoFixture(("src/app.js", "export class App {}\n"));
        var cyclePath = Path.Combine(repoDir, "src", "cycle");
        try
        {
            Directory.CreateSymbolicLink(cyclePath, repoDir);
        }
        catch (Exception)
        {
            return; // symlink creation can need elevated privileges (e.g. Windows without dev mode) -- skip rather than fail CI for that
        }

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.Contains(graph.Nodes, n => n.Id == "src/app.js");
    }

    // U3: a moment.js-shaped file (license banner + a long minified line) gets a file node carrying
    // both Tier 0 shape marks, and no definition nodes (nothing meaningful for the definition-pattern
    // regex to match against minified content).
    [Fact]
    public void Extract_MarksVendoredShapeFile_AndSkipsDefinitionExtraction_ForMinifiedContent()
    {
        var repoDir = CreateRepoFixture(("moment.js",
            "/*! moment.js v2.29.4 | Copyright (c) 2024 Moment.js contributors */\n" +
            "function enable(){" + new string('a', 1200) + "}\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var fileNode = Assert.Single(graph.Nodes, n => n.Id == "moment.js");
        Assert.Contains("Minified", fileNode.Shape);
        Assert.Contains("Banner", fileNode.Shape);
        Assert.DoesNotContain(graph.Nodes, n => n.Id == "moment.js::enable");
    }

    // U3 / E18: two files differing only by case must not crash the whole repo's extraction --
    // one is kept, the rest of the repo still extracts normally.
    [Fact]
    public void Extract_DoesNotThrow_OnCaseOnlyPathCollision()
    {
        var repoDir = CreateRepoFixture(
            ("Auth.cs", "namespace Demo;\npublic class Auth {}\n"),
            ("auth.cs", "namespace Demo;\npublic class AuthLower {}\n"),
            ("src/app.js", "export class App {}\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.Contains(graph.Nodes, n => n.Id == "src/app.js");
        // Exactly one of the case-colliding pair survived -- which one is unspecified, but there must
        // be exactly one file node at this path, not two and not a crash.
        Assert.Single(graph.Nodes, n => string.Equals(n.Id, "Auth.cs", StringComparison.OrdinalIgnoreCase) && n.SourceLocation == "L1");
    }

    // U3: an oversized file (many short lines, so it does NOT also trip the Minified heuristic) still
    // gets a file node with the Oversized shape mark, with definition extraction still running --
    // Oversized alone doesn't disable it, only Minified does, per this unit's scope.
    [Fact]
    public void Extract_MarksOversizedFile_AndStillAdmitsIt()
    {
        var padding = string.Join('\n', Enumerable.Repeat("    public void Pad() { DoNothing(); }", 30_000));
        var repoDir = CreateRepoFixture(("Big.cs", "namespace Demo;\npublic class Big {}\n" + padding));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var fileNode = Assert.Single(graph.Nodes, n => n.Id == "Big.cs");
        Assert.Contains("Oversized", fileNode.Shape);
        Assert.DoesNotContain("Minified", fileNode.Shape);
        Assert.Contains(graph.Nodes, n => n.Id == "Big.cs::Big");
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

    [Fact]
    public void Extract_CapturesPythonDefinitionDocstring_AsDefinitionNodeSummary()
    {
        var repoDir = CreateRepoFixture(("hooks.py",
            "class SignatureCheck:\n    \"\"\"Rejects webhooks whose\n    signature does not match.\"\"\"\n\n    def verify(self):\n        '''Compares the HMAC.'''\n        return True\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.Equal("Rejects webhooks whose signature does not match.", graph.Nodes.Single(n => n.Id == "hooks.py::SignatureCheck").Summary);
        Assert.Equal("Compares the HMAC.", graph.Nodes.Single(n => n.Id == "hooks.py::verify").Summary);
    }

    // A ':' inside annotations, defaults or brackets must not be taken for the end of the signature.
    [Fact]
    public void Extract_CapturesPythonDocstring_AfterMultiLineAnnotatedSignature()
    {
        var repoDir = CreateRepoFixture(("svc.py",
            "async def fetch(\n    url: str,\n    headers: dict[str, str] = {\"a\": \"b\"},\n) -> list[int]:\n    \"\"\"Downloads the page.\"\"\"\n    return []\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.Equal("Downloads the page.", graph.Nodes.Single(n => n.Id == "svc.py::fetch").Summary);
    }

    [Fact]
    public void Extract_LeavesPythonSummaryEmpty_WhenBodyDoesNotStartWithDocstring()
    {
        var repoDir = CreateRepoFixture(("svc.py", "def run():\n    x = \"\"\"not a docstring\"\"\"\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.Equal("", graph.Nodes.Single(n => n.Id == "svc.py::run").Summary);
    }

    [Fact]
    public void Extract_SplitsIdentifiersAndPathsIntoNameTerms()
    {
        var repoDir = CreateRepoFixture(
            ("django/middleware/csrf.py", "class CsrfViewMiddleware:\n    pass\n\ndef get_object_or_404():\n    pass\n"),
            ("src/Hashers.cs", "public class PBKDF2PasswordHasher { }\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.Equal("csrfviewmiddleware csrf view middleware", graph.Nodes.Single(n => n.Id == "django/middleware/csrf.py::CsrfViewMiddleware").NameTerms);
        Assert.Equal("get_object_or_404 get object or 404", graph.Nodes.Single(n => n.Id == "django/middleware/csrf.py::get_object_or_404").NameTerms);
        Assert.Equal("pbkdf2passwordhasher pbkdf2 password hasher", graph.Nodes.Single(n => n.Id == "src/Hashers.cs::PBKDF2PasswordHasher").NameTerms);
        Assert.Equal("django middleware csrf", graph.Nodes.Single(n => n.Id == "django/middleware/csrf.py").NameTerms);
    }

    [Fact]
    public void Extract_KeepsAOneWordNameAsItsOnlyNameTerm()
    {
        var repoDir = CreateRepoFixture(("signing.py", "class Signer:\n    pass\n"));

        Assert.Equal("signer", CodeGraphExtractor.Extract(repoDir).Nodes.Single(n => n.Id == "signing.py::Signer").NameTerms);
    }

    [Fact]
    public void Extract_BodyTermsMakeAnUndocumentedClassFindableByWhatItDoes()
    {
        // Django's SecurityMiddleware has no docstring; what it does is only in its methods' bodies.
        const string source = """
            class SecurityMiddleware(MiddlewareMixin):
                async_capable = False

                def __init__(self, get_response):
                    self.sts_seconds = settings.SECURE_HSTS_SECONDS

                def process_response(self, request, response):
                    if self.sts_seconds:
                        response.headers["Strict-Transport-Security"] = "max-age=%s" % self.sts_seconds
                    return response


            def unrelated_helper():
                return "cookie banner"
            """;
        var graph = CodeGraphExtractor.Extract(CreateRepoFixture(("django/middleware/security.py", source)));

        var classTerms = graph.Nodes.Single(n => n.Id == "django/middleware/security.py::SecurityMiddleware").BodyTerms.Split(' ');
        Assert.Contains("hsts", classTerms);
        Assert.Contains("transport", classTerms);
        Assert.Contains("middleware", classTerms); // the base class on the definition line
        Assert.DoesNotContain("cookie", classTerms); // the next top-level definition is not part of the class
        Assert.DoesNotContain("self", classTerms);
        Assert.DoesNotContain("return", classTerms);

        // A method's body stops at the next method.
        var initTerms = graph.Nodes.Single(n => n.Id == "django/middleware/security.py::__init__").BodyTerms.Split(' ');
        Assert.Contains("hsts", initTerms);
        Assert.DoesNotContain("transport", initTerms);
    }

    [Fact]
    public void Extract_BodyTermsWorkForBraceLanguages()
    {
        const string source = """
            namespace Demo
            {
                public class ReceiptPrinter
                {
                    public void Print(Invoice invoice)
                    {
                        // Sends the invoice to the thermal printer queue.
                        _queue.Enqueue(invoice.ToThermalFormat());
                    }
                }

                public class Unrelated
                {
                    private const string Banner = "cookie banner";
                }
            }
            """;
        var graph = CodeGraphExtractor.Extract(CreateRepoFixture(("src/ReceiptPrinter.cs", source)));

        var terms = graph.Nodes.Single(n => n.Id == "src/ReceiptPrinter.cs::ReceiptPrinter").BodyTerms.Split(' ');
        Assert.Contains("thermal", terms);
        Assert.Contains("invoice", terms);
        Assert.Equal("invoice", terms[0]); // most frequent first
        Assert.DoesNotContain("cookie", terms);
        Assert.DoesNotContain("public", terms);
    }

    [Fact]
    public void Extract_GivesEndpointsNameTerms()
    {
        const string source = """
            [Route("api/orders")]
            public class OrdersController : ControllerBase
            {
                [HttpGet("{id}")]
                public IActionResult Get(int id) => Ok();
            }
            """;
        var graph = CodeGraphExtractor.Extract(CreateRepoFixture(("Controllers/OrdersController.cs", source)));

        Assert.Equal("get api orders id", graph.Nodes.Single(n => n.Id.StartsWith("endpoint::", StringComparison.Ordinal)).NameTerms);
    }

    [Theory]
    [InlineData("tests/cache/test_db.py", true)]
    [InlineData("django/contrib/auth/test_hashers.py", true)]
    [InlineData("pkg/server_test.go", true)]
    [InlineData("backend/SupportForge.Api.Tests/Agents/DrafterAgentTests.cs", true)]
    [InlineData("src/OrderServiceTests.cs", true)]
    [InlineData("web/app/page.spec.ts", true)]
    [InlineData("web/__tests__/page.tsx", true)]
    [InlineData("conftest.py", true)]
    [InlineData("src/Latest.cs", false)]
    [InlineData("src/Contest.java", false)]
    [InlineData("django/core/paginator.py", false)]
    [InlineData("src/testimonials.ts", false)]
    public void IsTestPath_RecognisesTestDirectoriesAndFileNames(string path, bool expected)
    {
        Assert.Equal(expected, CodeGraphExtractor.IsTestPath(path));
    }

    [Fact]
    public void Extract_MarksTestNodes()
    {
        var repoDir = CreateRepoFixture(
            ("tests/test_cache.py", "def test_get():\n    pass\n"),
            ("app/cache.py", "def get():\n    pass\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        Assert.True(graph.Nodes.Single(n => n.Id == "tests/test_cache.py").IsTest);
        Assert.True(graph.Nodes.Single(n => n.Id == "tests/test_cache.py::test_get").IsTest);
        Assert.False(graph.Nodes.Single(n => n.Id == "app/cache.py::get").IsTest);
    }

    [Fact]
    public void Extract_StoresDefinitionExcerpt_FromItsFirstLine()
    {
        var body = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"    x{i} = {i}"));
        var repoDir = CreateRepoFixture(("mod.py", $"import os\n\n\ndef build():\n{body}\n"));

        var graph = CodeGraphExtractor.Extract(repoDir);

        var excerpt = graph.Nodes.Single(n => n.Id == "mod.py::build").Excerpt;
        Assert.StartsWith("def build():", excerpt);
        Assert.True(excerpt.Split('\n').Length <= 12);
        Assert.Equal("", graph.Nodes.Single(n => n.Id == "mod.py").Excerpt);
    }
}
