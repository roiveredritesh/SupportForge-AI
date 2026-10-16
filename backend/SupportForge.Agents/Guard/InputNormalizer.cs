using System.Text;
using System.Text.RegularExpressions;

namespace SupportForge.Agents.Guard;

/// <summary>
/// Produces the screened copy of a turn. The pipeline still receives the original text; this copy
/// only exists so that the guards see through common obfuscation (fullwidth letters, zero-width
/// splits, one level of base64).
/// </summary>
public static class InputNormalizer
{
    private const int MaxSegments = 5;
    private const int MaxSegmentChars = 2000;

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.CultureInvariant);

    // A run this long is far more likely to be an encoded payload than prose. Shorter runs are
    // ignored so that ordinary tokens (hashes, IDs) don't produce decoded segments.
    private static readonly Regex Base64Run = new(@"[A-Za-z0-9+/]{40,}={0,2}", RegexOptions.CultureInvariant);

    // Throwing decoder: random binary that happens to be base64-shaped must not count as text.
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static NormalizedInput Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return new NormalizedInput("", []);

        var cleaned = StripInvisible(Nfkc(text));
        cleaned = WhitespaceRun.Replace(cleaned, " ").Trim();

        var segments = new List<string>();
        foreach (Match match in Base64Run.Matches(cleaned))
        {
            if (segments.Count >= MaxSegments) break;

            var decoded = TryDecodePrintableUtf8(match.Value);
            if (decoded is null) continue;

            segments.Add(decoded.Length > MaxSegmentChars ? decoded[..MaxSegmentChars] : decoded);
        }

        return new NormalizedInput(cleaned, segments);
    }

    private static string Nfkc(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Lone surrogates make Normalize throw; keep the raw text rather than failing the turn.
            return text;
        }
    }

    private static string StripInvisible(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!IsInvisible(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool IsInvisible(char c) =>
        c is '​' or '‌' or '‍' or '⁠' or '﻿'
        || (c >= '‪' && c <= '‮')
        || (c >= '⁦' && c <= '⁩');

    private static string? TryDecodePrintableUtf8(string base64)
    {
        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        foreach (var c in decoded)
        {
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t')) return null;
        }

        return decoded;
    }
}
