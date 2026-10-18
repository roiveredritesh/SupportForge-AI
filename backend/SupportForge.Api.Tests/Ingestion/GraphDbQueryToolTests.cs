using SupportForge.Ingestion.Graph;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class GraphDbQueryToolTests
{
    [Theory]
    [InlineData("What files exist under src/mcp?", @"What files exist under src\/mcp\?")]
    [InlineData("What is \"gbrain\"?", @"What is \""gbrain\""\?")]
    [InlineData("foo && bar || (baz)", @"foo \&\& bar \|\| \(baz\)")]
    [InlineData("plain question with no special chars", "plain question with no special chars")]
    public void EscapeLuceneQuery_EscapesLuceneSpecialChars(string input, string expected)
    {
        Assert.Equal(expected, GraphDbQueryTool.EscapeLuceneQuery(input));
    }

    // U1: all matches score below the absolute floor -- every match must be rejected, same as a
    // zero-match result. 3.03 and 1.94 are the real live-observed noise scores that motivated this
    // fix (see GraphDbQueryTool's MinimumAbsoluteScore comment).
    [Theory]
    [InlineData(3.03, 3.03)]
    [InlineData(1.94, 3.03)]
    public void ClearsRelevanceFloor_RejectsMatchBelowAbsoluteFloor_EvenAtTopScore(double score, double topScore)
    {
        Assert.False(GraphDbQueryTool.ClearsRelevanceFloor(score, topScore));
    }

    // A low-scoring result set (topScore itself below the absolute floor) demonstrates why relative
    // alone is insufficient: 2.8 clears the relative floor against a topScore of 4.0 (0.65 * 4.0 =
    // 2.6, so 2.8 >= 2.6) but must still be rejected because it never clears the absolute floor.
    [Fact]
    public void ClearsRelevanceFloor_RelativeFloorClearedAloneIsNotEnough()
    {
        Assert.False(GraphDbQueryTool.ClearsRelevanceFloor(2.8, 4.0));
    }

    // Regression guard: multiple genuinely strong matches (both floors cleared) all survive --
    // this change must not over-tighten and lose recall on an already-good result set.
    [Theory]
    [InlineData(6.0, 6.0)]
    [InlineData(4.5, 6.0)]
    [InlineData(4.0, 6.0)]
    public void ClearsRelevanceFloor_AcceptsMatchesClearingBothFloors(double score, double topScore)
    {
        Assert.True(GraphDbQueryTool.ClearsRelevanceFloor(score, topScore));
    }

    [Fact]
    public void BuildSearchQuery_KeepsMeaningfulWords_AndAddsStemPrefixes()
    {
        var terms = GraphDbQueryTool.BuildSearchQuery("Which middleware protects against cross-site request forgery?").Split(' ');

        Assert.Contains("middleware", terms);
        Assert.Contains("forgery", terms);
        Assert.Contains("protect*", terms);
        Assert.DoesNotContain("which", terms);
    }

    [Fact]
    public void BuildSearchQuery_KeepsAnIdentifierWholeAndSplit()
    {
        var terms = GraphDbQueryTool.BuildSearchQuery("What does CsrfViewMiddleware do?").Split(' ');

        Assert.Contains("csrfviewmiddleware", terms);
        Assert.Contains("csrf", terms);
        Assert.Contains("view", terms);
        Assert.Contains("middleware", terms);
    }

    [Fact]
    public void BuildSearchQuery_DropsCodeQuestionBoilerplate()
    {
        Assert.Equal("", GraphDbQueryTool.BuildSearchQuery("Which class or function does this?"));
    }

    [Fact]
    public void BuildSearchQuery_CapsTheNumberOfTerms()
    {
        var question = string.Join(' ', Enumerable.Range(0, 40).Select(i => $"term{i}x"));

        Assert.True(GraphDbQueryTool.BuildSearchQuery(question).Split(' ').Length <= 12);
    }

    [Theory]
    [InlineData("Which test covers the refund path?", true)]
    [InlineData("Where are the unit tests for checkout?", true)]
    [InlineData("Which middleware protects against CSRF?", false)]
    [InlineData("What does the contest scoring do?", false)]
    public void AsksAboutTests_DetectsQuestionsAboutTestCode(string question, bool expected)
    {
        Assert.Equal(expected, GraphDbQueryTool.AsksAboutTests(question));
    }

    [Fact]
    public void FieldedQuery_SearchesEveryFieldAndWeightsBodyTermsDown()
    {
        Assert.Equal(
            "nameTerms:(csrf cross*) summary:(csrf cross*) purpose:(csrf cross*) domainTerms:(csrf cross*) bodyTerms:(csrf cross*)^0.4",
            GraphDbQueryTool.FieldedQuery("csrf cross*", 0.4));
    }

    [Fact]
    public void SearchIndex_DoesNotIndexTheRawLabel()
    {
        Assert.DoesNotContain("n.label", GraphDbQueryTool.CreateSearchIndexCypher);
        Assert.Contains("n.bodyTerms", GraphDbQueryTool.CreateSearchIndexCypher);
    }
}
