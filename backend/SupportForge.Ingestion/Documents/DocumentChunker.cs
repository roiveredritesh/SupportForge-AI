namespace SupportForge.Ingestion.Documents;

public static class DocumentChunker
{
    // ponytail: prose/markdown (tables, backticks, punctuation) tokenizes denser than plain code —
    // 1500 chars of a markdown table measured at 576 tokens, over NIM's 512-token embedding limit and
    // rejected with 400. 1000 matches the margin CodeIngestionJob's chunker already uses for the same
    // failure mode; bump only with a fresh token-count check against the live embedding model.
    public static List<string> Chunk(string text, int maxChars = 1000)
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
                current.Clear();
                currentLength = 0;
            }

            current.Add(word);
            currentLength += word.Length + 1;
        }

        if (current.Count > 0) chunks.Add(string.Join(' ', current));
        return chunks;
    }
}
