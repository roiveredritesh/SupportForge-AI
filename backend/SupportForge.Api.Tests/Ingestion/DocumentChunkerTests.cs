using SupportForge.Ingestion.Documents;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class DocumentChunkerTests
{
    [Fact]
    public void Chunk_SplitsLongText_IntoChunksUnderMaxSize()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 500)); // ~2500 chars
        var chunks = DocumentChunker.Chunk(text, maxChars: 500);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 500));
        Assert.Equal(text, string.Join(" ", chunks).Replace("  ", " ").Trim());
    }
}
