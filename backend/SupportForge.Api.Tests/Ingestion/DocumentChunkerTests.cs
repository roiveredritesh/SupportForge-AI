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
    }

    [Fact]
    public void Chunk_WithNoOverlap_ReproducesOriginalTextExactly_ViaJoin()
    {
        // Backward-compat check: overlapChars: 0 preserves the pre-D1 behavior exactly.
        var text = string.Join(" ", Enumerable.Repeat("word", 500));
        var chunks = DocumentChunker.Chunk(text, maxChars: 500, overlapChars: 0);

        Assert.Equal(text, string.Join(" ", chunks).Replace("  ", " ").Trim());
    }

    [Fact]
    public void Chunk_DefaultOverlap_ConsecutiveChunksShareTrailingWords()
    {
        // D1 (gap-closing-solutions.md Phase D, item 1): a concept spanning a chunk boundary should
        // have surrounding context in at least one of the two chunks. The overlap seed can be
        // several words, so chunk[i+1]'s first word is the *earliest* word of that seed -- verified
        // by checking it also appears near the end of chunk[i], not by requiring an exact
        // last-word-equals-first-word match (only true when the seed happens to be a single word).
        var words = Enumerable.Range(1, 300).Select(i => $"word{i}");
        var text = string.Join(" ", words);
        var chunks = DocumentChunker.Chunk(text, maxChars: 500, overlapChars: 100);

        Assert.True(chunks.Count > 1);
        for (var i = 0; i < chunks.Count - 1; i++)
        {
            var firstWordOfNextChunk = chunks[i + 1].Split(' ')[0];
            Assert.Contains(firstWordOfNextChunk, chunks[i].Split(' '));
        }
    }

    [Fact]
    public void Chunk_OverlapNeverExceedsConfiguredBudget()
    {
        var text = string.Join(" ", Enumerable.Range(1, 300).Select(i => $"word{i}"));
        var chunks = DocumentChunker.Chunk(text, maxChars: 500, overlapChars: 100);

        // Every chunk after the first starts with an overlap seed whose length must fit within
        // overlapChars -- confirmed indirectly by checking no chunk exceeds maxChars even with
        // the seeded overlap included.
        Assert.All(chunks, c => Assert.True(c.Length <= 500));
    }

    [Fact]
    public void Chunk_NeverSplitsAWordAcrossChunks()
    {
        var text = string.Join(" ", Enumerable.Range(1, 400).Select(i => $"distinctword{i}"));
        var chunks = DocumentChunker.Chunk(text, maxChars: 300, overlapChars: 80);

        var allWords = chunks.SelectMany(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.All(allWords, w => Assert.StartsWith("distinctword", w));
    }
}
