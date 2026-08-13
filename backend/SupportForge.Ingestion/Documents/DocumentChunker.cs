namespace SupportForge.Ingestion.Documents;

public static class DocumentChunker
{
    // ponytail: prose/markdown (tables, backticks, punctuation) tokenizes denser than plain code —
    // 1500 chars of a markdown table measured at 576 tokens, over NIM's 512-token embedding limit and
    // rejected with 400. 1000 matches the margin CodeIngestionJob's chunker already uses for the same
    // failure mode; bump only with a fresh token-count check against the live embedding model.
    //
    // D1 (gap-closing-solutions.md Phase D, item 1): overlapChars carries the trailing ~150 chars of
    // one chunk into the start of the next, so a concept spanning a chunk boundary still has
    // surrounding context in at least one of the two chunks that would otherwise get it truncated
    // out of both. Word-based (never splits mid-word), same as the chunking itself.
    public static List<string> Chunk(string text, int maxChars = 1000, int overlapChars = 150)
    {
        var chunks = new List<string>();
        var current = new List<string>();
        var currentLength = 0;

        // U5: Markdown tables are never split mid-table. A table that fits within maxChars is
        // treated as one atomic unit alongside the word-based accumulator below; a table that
        // exceeds maxChars on its own is split row-wise with the header + separator repeated.
        foreach (var (segmentText, isTable) in SplitIntoSegments(text))
        {
            if (isTable && segmentText.Length <= maxChars)
            {
                if (currentLength + segmentText.Length + 1 > maxChars && current.Count > 0)
                {
                    chunks.Add(string.Join(' ', current));
                    (current, currentLength) = TakeOverlapTail(current, overlapChars);
                }

                current.Add(segmentText);
                currentLength += segmentText.Length + 1;
                continue;
            }

            if (isTable)
            {
                if (current.Count > 0)
                {
                    chunks.Add(string.Join(' ', current));
                    current = new List<string>();
                    currentLength = 0;
                }

                chunks.AddRange(SplitTableRowWise(segmentText, maxChars));
                continue;
            }

            foreach (var word in segmentText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (currentLength + word.Length + 1 > maxChars && current.Count > 0)
                {
                    chunks.Add(string.Join(' ', current));
                    (current, currentLength) = TakeOverlapTail(current, overlapChars);
                }

                current.Add(word);
                currentLength += word.Length + 1;
            }
        }

        if (current.Count > 0) chunks.Add(string.Join(' ', current));
        return chunks;
    }

    // Walks the text line by line, grouping it into alternating table / non-table segments.
    // A run of `|`-prefixed lines only counts as a table when its second line is a valid
    // separator row (e.g. "|---|---|") -- otherwise it's a malformed table and falls back into
    // the surrounding non-table text so the normal word-based split handles it (no special-case
    // path that could infinite-loop on it).
    private static List<(string Text, bool IsTable)> SplitIntoSegments(string text)
    {
        var lines = text.Split('\n');
        var segments = new List<(string Text, bool IsTable)>();
        var buffer = new List<string>();
        var i = 0;

        while (i < lines.Length)
        {
            if (!IsTableLine(lines[i]))
            {
                buffer.Add(lines[i]);
                i++;
                continue;
            }

            var start = i;
            while (i < lines.Length && IsTableLine(lines[i])) i++;
            var block = lines[start..i];

            if (block.Length >= 2 && IsSeparatorRow(block[1]))
            {
                if (buffer.Count > 0)
                {
                    segments.Add((string.Join('\n', buffer), false));
                    buffer.Clear();
                }
                segments.Add((string.Join('\n', block), true));
            }
            else
            {
                buffer.AddRange(block);
            }
        }

        if (buffer.Count > 0) segments.Add((string.Join('\n', buffer), false));
        return segments;
    }

    private static bool IsTableLine(string line) => line.TrimStart().StartsWith('|');

    private static bool IsSeparatorRow(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('|') || !trimmed.Contains('-')) return false;

        foreach (var c in trimmed)
        {
            if (c is not ('|' or ':' or '-' or ' ')) return false;
        }

        return true;
    }

    // Splits an over-sized table row-wise (never mid-row), repeating the header + separator
    // rows at the start of every sub-chunk after the first so each piece is still a readable
    // table on its own.
    private static List<string> SplitTableRowWise(string tableText, int maxChars)
    {
        var lines = tableText.Split('\n');
        var header = lines[0];
        var separator = lines[1];
        var headerLength = header.Length + 1 + separator.Length + 1;

        var result = new List<string>();
        var current = new List<string> { header, separator };
        var currentLength = headerLength;

        for (var i = 2; i < lines.Length; i++)
        {
            var row = lines[i];
            if (currentLength + row.Length + 1 > maxChars && current.Count > 2)
            {
                result.Add(string.Join('\n', current));
                current = new List<string> { header, separator };
                currentLength = headerLength;
            }

            current.Add(row);
            currentLength += row.Length + 1;
        }

        result.Add(string.Join('\n', current));
        return result;
    }

    // Walks backward from the end of a just-finished chunk, collecting whole words until adding
    // one more would exceed overlapChars -- becomes the seed for the next chunk.
    private static (List<string> Words, int Length) TakeOverlapTail(List<string> words, int overlapChars)
    {
        var tail = new List<string>();
        var length = 0;
        if (overlapChars <= 0) return (tail, length);

        for (var i = words.Count - 1; i >= 0; i--)
        {
            var word = words[i];
            if (length + word.Length + 1 > overlapChars && tail.Count > 0) break;
            tail.Insert(0, word);
            length += word.Length + 1;
        }
        return (tail, length);
    }
}
