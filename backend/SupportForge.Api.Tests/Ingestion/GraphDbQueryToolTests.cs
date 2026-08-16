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
}
