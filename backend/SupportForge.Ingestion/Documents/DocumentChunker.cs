namespace SupportForge.Ingestion.Documents;

public static class DocumentChunker
{
    public static List<string> Chunk(string text, int maxChars = 1500)
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
