using System.Text.RegularExpressions;

namespace SupportForge.Agents.Verification;

// U13 (JEV intent classification plan, R18): splits a draft into the sentences that each get one Noul. This is a small,
// deterministic splitter, not an NLP library. It splits on . ? ! and newlines, and it leaves alone decimals and versions
// ("v2.1", "3.5"), dots inside a token ("example.com") and a short list of abbreviations. Sentences under four words are
// dropped, because a fragment such as "Yes." cannot be meaningfully checked against the retrieved context.
public static class SentenceSplitter
{
    private const int MinWords = 4;

    // Compared case-insensitively, without the final dot. "e.g" and "i.e" are the dotted forms of the Latin abbreviations.
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g", "i.e", "etc", "vs", "mr", "mrs", "dr", "approx",
    };

    // A Markdown list marker at the start of a line: "- ", "* ", "1. " or "1) ".
    private static readonly Regex ListMarker = new(@"^(?:[-*]|\d+[.)])\s+", RegexOptions.Compiled);

    public static IReadOnlyList<string> Split(string text, int maxSentences)
    {
        var sentences = new List<string>();
        if (string.IsNullOrWhiteSpace(text) || maxSentences <= 0)
        {
            return sentences;
        }

        // A newline always ends a sentence, so each line is split on its own.
        foreach (var rawLine in text.Split('\n'))
        {
            if (sentences.Count >= maxSentences)
            {
                break;
            }

            var line = rawLine.Trim();
            var marker = ListMarker.Match(line);
            if (marker.Success)
            {
                line = line[marker.Length..];
            }

            SplitLine(line, sentences, maxSentences);
        }

        return sentences;
    }

    private static void SplitLine(string line, List<string> sentences, int maxSentences)
    {
        var start = 0;
        var i = 0;
        while (i < line.Length && sentences.Count < maxSentences)
        {
            if (!IsTerminator(line, i))
            {
                i++;
                continue;
            }

            // A run of terminators ("?!", "...") and any closing quotes or brackets belong to the sentence they end.
            var end = i + 1;
            while (end < line.Length && IsTerminatorChar(line[end]))
            {
                end++;
            }

            while (end < line.Length && IsCloser(line[end]))
            {
                end++;
            }

            // A terminator inside a token ("example.com") is not a boundary. It has to be followed by whitespace or the end.
            if (end == line.Length || char.IsWhiteSpace(line[end]))
            {
                Add(line[start..end], sentences);
                start = end;
            }

            i = end;
        }

        if (start < line.Length && sentences.Count < maxSentences)
        {
            Add(line[start..], sentences);
        }
    }

    private static bool IsTerminator(string line, int i)
    {
        var c = line[i];
        if (c is '?' or '!')
        {
            return true;
        }

        if (c != '.')
        {
            return false;
        }

        // "v2.1" and "3.5": a dot between two digits is part of a number or a version.
        if (i > 0 && i + 1 < line.Length && char.IsDigit(line[i - 1]) && char.IsDigit(line[i + 1]))
        {
            return false;
        }

        // "e.g.", "Dr." and the other allowlisted abbreviations do not end a sentence.
        return !Abbreviations.Contains(WordBefore(line, i));
    }

    // The word that ends just before the position `dot`, with a leading bracket or quote removed.
    private static string WordBefore(string line, int dot)
    {
        var start = dot;
        while (start > 0 && !char.IsWhiteSpace(line[start - 1]))
        {
            start--;
        }

        return line[start..dot].TrimStart('(', '[', '"', '\'');
    }

    private static void Add(string segment, List<string> sentences)
    {
        var sentence = segment.Trim();
        if (CountWords(sentence) >= MinWords)
        {
            sentences.Add(sentence);
        }
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static bool IsTerminatorChar(char c) => c is '.' or '?' or '!';

    private static bool IsCloser(char c) => c is ')' or ']' or '}' or '"' or '\'';
}
