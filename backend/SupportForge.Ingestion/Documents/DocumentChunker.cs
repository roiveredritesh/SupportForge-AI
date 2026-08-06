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
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<string>();
        var current = new List<string>();
        var currentLength = 0;

        foreach (var word in words)
        {
            if (currentLength + word.Length + 1 > maxChars && current.Count > 0)
            {
                chunks.Add(string.Join(' ', current));
                (current, currentLength) = TakeOverlapTail(current, overlapChars);
            }

            current.Add(word);
            currentLength += word.Length + 1;
        }

        if (current.Count > 0) chunks.Add(string.Join(' ', current));
        return chunks;
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
