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
}
