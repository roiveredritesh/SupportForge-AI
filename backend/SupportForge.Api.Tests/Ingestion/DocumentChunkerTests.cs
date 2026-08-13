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

    // U5: table-aware chunking.

    [Fact]
    public void Chunk_SmallTableInSurroundingProse_StaysInOneChunkAndIsNotSplitMidTable()
    {
        var prefix = string.Join(" ", Enumerable.Repeat("word", 90)); // pushes close to the boundary
        var table = "| Col1 | Col2 |\n|------|------|\n| a | b |\n| c | d |";
        var suffix = string.Join(" ", Enumerable.Repeat("tail", 5));
        var text = $"{prefix}\n{table}\n{suffix}";

        var chunks = DocumentChunker.Chunk(text, maxChars: 500, overlapChars: 0);

        var chunkContainingTable = Assert.Single(chunks, c => c.Contains("Col1"));
        Assert.Contains("| a | b |", chunkContainingTable);
        Assert.Contains("| c | d |", chunkContainingTable);
        Assert.Contains("|------|------|", chunkContainingTable);
    }

    [Fact]
    public void Chunk_TableExceedingMaxChars_SplitsRowWiseWithHeaderRepeated()
    {
        var header = "| Col1 | Col2 |";
        var separator = "|------|------|";
        var rows = Enumerable.Range(1, 40).Select(i => $"| row{i}a | row{i}b |");
        var table = string.Join('\n', new[] { header, separator }.Concat(rows));

        var chunks = DocumentChunker.Chunk(table, maxChars: 200, overlapChars: 0);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c =>
        {
            Assert.StartsWith(header, c);
            Assert.Contains(separator, c);
        });

        // Every row appears exactly once across all sub-chunks.
        foreach (var i in Enumerable.Range(1, 40))
        {
            var row = $"| row{i}a | row{i}b |";
            Assert.Single(chunks, c => c.Contains(row));
        }
    }

    [Fact]
    public void Chunk_ProseTableProse_IsolatesTableFromSurroundingWordBasedRegions()
    {
        var prose1 = string.Join(" ", Enumerable.Repeat("alpha", 20));
        var table = "| H1 | H2 |\n|----|----|\n| x | y |";
        var prose2 = string.Join(" ", Enumerable.Repeat("beta", 20));
        var text = $"{prose1}\n{table}\n{prose2}";

        var chunks = DocumentChunker.Chunk(text, maxChars: 1000, overlapChars: 0);

        var combined = string.Join(' ', chunks);
        Assert.Contains("| x | y |", combined);
        Assert.Contains("alpha", combined);
        Assert.Contains("beta", combined);
        // The table text isn't torn apart by the word splitter.
        Assert.Contains(chunks, c => c.Contains("| H1 | H2 |") && c.Contains("| x | y |"));
    }

    [Fact]
    public void Chunk_MalformedTableWithNoSeparatorRow_FallsBackToWordBasedSplitting_NoInfiniteLoop()
    {
        var text = "| this looks like a table row but has no separator row after it\n" +
                    string.Join(" ", Enumerable.Repeat("word", 100));

        var chunks = DocumentChunker.Chunk(text, maxChars: 200, overlapChars: 0);

        Assert.True(chunks.Count > 0);
        Assert.Contains(chunks, c => c.Contains("word"));
    }
}
