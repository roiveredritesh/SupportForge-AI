using System.Text.RegularExpressions;
using SupportForge.Agents;

namespace SupportForge.Ingestion.Documents;

/// <summary>
/// Resolves U1's <see cref="ImageCaptionCandidate"/> tokens into real captions via the vision LLM
/// seam (KTD3), with a safe placeholder fallback whenever captioning isn't possible. Owns no
/// <see cref="HttpClient"/> -- every byte fetch is the caller's responsibility (U3/U4 differ in how
/// they fetch attachment vs. external images), so this class only knows how to turn bytes into a
/// caption, never how to get the bytes.
/// </summary>
public sealed class IngestionImageCaptioner
{
    private const string IngestionPrompt =
        "Describe the content of this diagram/image as it would appear in product documentation -- " +
        "labels, flow, structure -- in plain text suitable for a knowledge base.";

    // KTD11: the vision model's own output is untrusted derived text -- an image can contain visible
    // text instructing the model to emit an injected instruction, which would otherwise be spliced
    // verbatim into indexed KB content. Same trust boundary as U1's script/style guard, applied to
    // model output instead of raw HTML. Not a full injection classifier -- a basic phrase filter per
    // the plan's intent.
    private static readonly Regex InjectionPhrase = new(
        "ignore (all )?(previous|prior) instructions|disregard the above|you are now|^system:|new instructions:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly (byte[] Magic, string Kind)[] ImageSignatures =
    [
        ([0x89, 0x50, 0x4E, 0x47], "png"),
        ([0xFF, 0xD8, 0xFF], "jpeg"),
        ([0x47, 0x49, 0x46, 0x38], "gif"),
        ([0x42, 0x4D], "bmp"),
        // WEBP: "RIFF"....."WEBP" -- checked separately below since the magic isn't contiguous.
    ];

    private readonly ILlmChatClient _llm;

    public IngestionImageCaptioner(ILlmChatClient llm) => _llm = llm;

    /// <summary>
    /// Resolves one caption-candidate token to its replacement text. Never throws -- any failure
    /// (unsupported vision model, byte-fetch failure, non-image content, provider rejection) falls
    /// back to a placeholder so a single bad image never fails the whole page (mirrors R7's
    /// fault-isolation spirit).
    /// </summary>
    public async Task<string> CaptionAsync(
        ImageCaptionCandidate token,
        Func<CancellationToken, Task<byte[]>> resolveBytesAsync,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(resolveBytesAsync);

        var isDiagram = token.Kind == ImageSourceKind.Attachment;
        var placeholder = Placeholder(isDiagram, token.AltOrName ?? token.SourceRef);

        if (!_llm.SupportsVision) return placeholder;

        byte[] bytes;
        try
        {
            bytes = await resolveBytesAsync(ct);
        }
        catch
        {
            return placeholder;
        }

        if (!TryDetectImageType(bytes, out _)) return placeholder;

        string caption;
        try
        {
            var base64 = Convert.ToBase64String(bytes);
            caption = await _llm.AnalyzeImageAsync(base64, IngestionPrompt, ct);
        }
        catch
        {
            return placeholder;
        }

        var sanitized = Sanitize(caption);
        return string.IsNullOrWhiteSpace(sanitized) ? placeholder : Placeholder(isDiagram, sanitized);
    }

    // Attachment-kind tokens (drawio/gliffy macros and Confluence ri:attachment images alike) are
    // treated as potentially-diagram content, since a diagram is exactly the case worth calling out
    // distinctly; External-kind tokens (plain <img> and ri:url references) are always plain images.
    // U1 doesn't carry enough shape distinction (e.g. no separate "macro name") past the token to
    // split attachment images from attachment diagrams more precisely -- this is the documented
    // judgment call the plan permits.
    private static string Placeholder(bool isDiagram, string name) =>
        isDiagram ? $"[diagram: {name}]" : $"[image: {name}]";

    private static bool TryDetectImageType(byte[] bytes, out string kind)
    {
        kind = "";
        foreach (var (magic, sig) in ImageSignatures)
        {
            if (bytes.Length >= magic.Length && bytes.AsSpan(0, magic.Length).SequenceEqual(magic))
            {
                kind = sig;
                return true;
            }
        }

        if (bytes.Length >= 12 &&
            bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
            bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P')
        {
            kind = "webp";
            return true;
        }

        return false;
    }

    private static string Sanitize(string caption)
    {
        if (string.IsNullOrWhiteSpace(caption)) return caption;

        // Drop whole sentences containing an instruction-like phrase rather than just the phrase
        // itself, so the surrounding sentence (which framed the injected instruction) doesn't survive.
        var sentences = Regex.Split(caption, @"(?<=[.!?])\s+");
        var kept = sentences.Where(s => !InjectionPhrase.IsMatch(s));
        var result = string.Join(" ", kept).Trim();
        return result;
    }
}
