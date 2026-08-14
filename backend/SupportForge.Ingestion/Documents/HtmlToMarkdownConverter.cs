using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace SupportForge.Ingestion.Documents;

/// <summary>Where an image/diagram caption candidate's underlying content lives (KTD10).</summary>
public enum ImageSourceKind
{
    /// <summary>Confluence <c>ri:attachment</c> reference, or a drawio/gliffy macro -- resolved via
    /// Confluence's authenticated attachment API by a later unit (U3).</summary>
    Attachment,

    /// <summary>Externally-hosted: a plain HTML &lt;img src="..."&gt; (always external) or a
    /// Confluence <c>ri:url</c> reference -- resolved via an unauthenticated, SSRF-checked fetch by
    /// a later unit (U3).</summary>
    External
}

/// <summary>
/// One image/diagram found in the source HTML that could not be converted to plain Markdown and was
/// instead left as a <c>{{IMAGE:...}}</c> token in <see cref="HtmlToMarkdownResult.Markdown"/>. U2's
/// captioner resolves these (via U3/U4) and substitutes a real caption for the token.
/// </summary>
/// <param name="Kind">Attachment vs. external -- decides how U3 fetches the image.</param>
/// <param name="SourceRef">Attachment filename/id for <see cref="ImageSourceKind.Attachment"/>, or the
/// URL for <see cref="ImageSourceKind.External"/>.</param>
/// <param name="AltOrName">Alt text / macro name, if any, for use as a caption fallback.</param>
public sealed record ImageCaptionCandidate(ImageSourceKind Kind, string SourceRef, string? AltOrName);

/// <summary>Conversion output: the Markdown text plus every caption-candidate token embedded in it.</summary>
public sealed record HtmlToMarkdownResult(string Markdown, IReadOnlyList<ImageCaptionCandidate> Images);

/// <summary>
/// Converts an <see cref="HtmlDocument"/> (plain HTML or Confluence storage-format XHTML) to
/// Markdown. Tables become GitHub-flavored Markdown tables; images, Confluence image references, and
/// drawio/gliffy diagram macros become <c>{{IMAGE:kind:ref|alt}}</c> caption-candidate tokens instead
/// of being silently dropped; everything else becomes plain Markdown/text.
/// </summary>
public static class HtmlToMarkdownConverter
{
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex ExcessBlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    public static HtmlToMarkdownResult Convert(HtmlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Same script/style removal WebsiteIngestionJob.ExtractVisibleText and ConfluencePageFetcher
        // already perform (R4): strips the tag AND its content so injected instruction text hidden in
        // a script/style block never reaches Markdown output, run before any conversion.
        foreach (var scriptOrStyle in document.DocumentNode.SelectNodes("//script|//style")?.ToList() ?? [])
            scriptOrStyle.Remove();

        var images = new List<ImageCaptionCandidate>();
        var sb = new StringBuilder();
        ConvertChildren(document.DocumentNode, sb, images);

        var markdown = ExcessBlankLines.Replace(sb.ToString(), "\n\n").Trim();
        return new HtmlToMarkdownResult(markdown, images);
    }

    private static void ConvertChildren(HtmlNode node, StringBuilder sb, List<ImageCaptionCandidate> images)
    {
        foreach (var child in node.ChildNodes)
            ConvertNode(child, sb, images);
    }

    private static void ConvertNode(HtmlNode node, StringBuilder sb, List<ImageCaptionCandidate> images)
    {
        if (node.NodeType == HtmlNodeType.Comment) return;

        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = WhitespaceRun.Replace(WebUtility.HtmlDecode(node.InnerText), " ");
            if (!string.IsNullOrWhiteSpace(text)) sb.Append(text);
            return;
        }

        switch (node.Name.ToLowerInvariant())
        {
            case "script":
            case "style":
                return; // already stripped above; guard kept in case a caller reuses this on raw HTML
            case "table":
                ConvertTable(node, sb, images);
                sb.Append('\n');
                return;
            case "img":
                EmitImgToken(node, sb, images);
                return;
            case "ac:image":
                EmitAcImageToken(node, sb, images);
                return;
            case "ac:structured-macro":
                EmitMacroToken(node, sb, images);
                return;
            case "br":
                sb.Append("  \n");
                return;
            case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                sb.Append('#', node.Name[1] - '0').Append(' ');
                ConvertChildren(node, sb, images);
                sb.Append("\n\n");
                return;
            case "p": case "div":
                ConvertChildren(node, sb, images);
                sb.Append("\n\n");
                return;
            case "b": case "strong":
                sb.Append("**"); ConvertChildren(node, sb, images); sb.Append("**");
                return;
            case "i": case "em":
                sb.Append('*'); ConvertChildren(node, sb, images); sb.Append('*');
                return;
            case "a":
                var start = sb.Length;
                ConvertChildren(node, sb, images);
                var linkText = sb.ToString(start, sb.Length - start);
                sb.Length = start;
                sb.Append('[').Append(linkText).Append("](").Append(node.GetAttributeValue("href", "")).Append(')');
                return;
            case "ul":
                ConvertList(node, sb, images, ordered: false);
                sb.Append('\n');
                return;
            case "ol":
                ConvertList(node, sb, images, ordered: true);
                sb.Append('\n');
                return;
            default:
                // Unrecognized wrapper (body, html, span, ac:layout, ...): recurse so its content
                // isn't lost even though the wrapper itself has no Markdown equivalent.
                ConvertChildren(node, sb, images);
                return;
        }
    }

    private static void ConvertList(HtmlNode listNode, StringBuilder sb, List<ImageCaptionCandidate> images, bool ordered)
    {
        var index = 1;
        foreach (var li in listNode.ChildNodes.Where(n => string.Equals(n.Name, "li", StringComparison.OrdinalIgnoreCase)))
        {
            sb.Append(ordered ? $"{index}. " : "- ");
            ConvertChildren(li, sb, images);
            sb.Append('\n');
            index++;
        }
    }

    // GitHub-flavored Markdown table: header row, "---" separator row, data rows. A table with no
    // <th> gets a synthesized empty header (still a well-formed GFM table) and every <tr> becomes a
    // data row.
    private static void ConvertTable(HtmlNode table, StringBuilder sb, List<ImageCaptionCandidate> images)
    {
        var rows = table.Descendants("tr").ToList();
        if (rows.Count == 0) return;

        var headerCells = rows[0].ChildNodes.Where(n => string.Equals(n.Name, "th", StringComparison.OrdinalIgnoreCase)).ToList();
        List<string> headerTexts;
        List<HtmlNode> dataRows;
        int columnCount;

        if (headerCells.Count > 0)
        {
            headerTexts = headerCells.Select(c => CellText(c, images)).ToList();
            columnCount = headerCells.Count;
            dataRows = rows.Skip(1).ToList();
        }
        else
        {
            columnCount = rows[0].ChildNodes.Count(n => n.Name is "td" or "th");
            headerTexts = Enumerable.Repeat(string.Empty, columnCount).ToList();
            dataRows = rows;
        }

        sb.Append('|').Append(string.Join('|', headerTexts.Select(h => $" {h} "))).Append("|\n");
        sb.Append('|').Append(string.Join('|', Enumerable.Repeat(" --- ", columnCount))).Append("|\n");

        foreach (var row in dataRows)
        {
            var cells = row.ChildNodes.Where(n => n.Name is "td" or "th").ToList();
            if (cells.Count == 0) continue;
            sb.Append('|').Append(string.Join('|', cells.Select(c => $" {CellText(c, images)} "))).Append("|\n");
        }
    }

    // Cell content is rendered through the normal converter (so e.g. <b> still becomes **bold**) then
    // flattened to one line and pipe-escaped, since a raw newline or unescaped "|" would break the row.
    private static string CellText(HtmlNode cell, List<ImageCaptionCandidate> images)
    {
        var sb = new StringBuilder();
        ConvertChildren(cell, sb, images);
        var text = WhitespaceRun.Replace(sb.ToString(), " ").Trim();
        return text.Replace("|", "\\|");
    }

    private static string FormatImageToken(ImageSourceKind kind, string sourceRef, string? altOrName) =>
        "{{IMAGE:" + (kind == ImageSourceKind.Attachment ? "attachment" : "external") + ":" + sourceRef + "|" + (altOrName ?? sourceRef) + "}}";

    private static void EmitImgToken(HtmlNode img, StringBuilder sb, List<ImageCaptionCandidate> images)
    {
        // Every plain HTML <img src="..."> is external by definition (KTD10) -- there's no
        // authenticated-attachment concept outside Confluence's storage format.
        var src = img.GetAttributeValue("src", "");
        var alt = img.GetAttributeValue("alt", null);
        images.Add(new ImageCaptionCandidate(ImageSourceKind.External, src, alt));
        sb.Append(FormatImageToken(ImageSourceKind.External, src, alt));
    }

    // Confluence storage format: <ac:image ac:alt="..."><ri:attachment ri:filename="..."/></ac:image>
    // or <ac:image><ri:url ri:value="..."/></ac:image>.
    private static void EmitAcImageToken(HtmlNode acImage, StringBuilder sb, List<ImageCaptionCandidate> images)
    {
        var alt = acImage.GetAttributeValue("ac:alt", null) ?? acImage.GetAttributeValue("alt", null);

        var attachment = FindDescendant(acImage, "ri:attachment");
        if (attachment is not null)
        {
            var filename = attachment.GetAttributeValue("ri:filename", null)
                ?? attachment.GetAttributeValue("ri:content-id", "attachment");
            images.Add(new ImageCaptionCandidate(ImageSourceKind.Attachment, filename, alt));
            sb.Append(FormatImageToken(ImageSourceKind.Attachment, filename, alt));
            return;
        }

        var url = FindDescendant(acImage, "ri:url");
        if (url is not null)
        {
            var href = url.GetAttributeValue("ri:value", "");
            images.Add(new ImageCaptionCandidate(ImageSourceKind.External, href, alt));
            sb.Append(FormatImageToken(ImageSourceKind.External, href, alt));
        }

        // Neither ri:attachment nor ri:url present: nothing resolvable, nothing emitted.
    }

    // drawio/gliffy render as <ac:structured-macro ac:name="drawio"><ac:parameter ac:name="diagramName">
    // Foo</ac:parameter>...</ac:structured-macro> -- always attachment-kind (KTD10), the diagram lives
    // in Confluence's attachment store, not an external URL.
    private static void EmitMacroToken(HtmlNode macro, StringBuilder sb, List<ImageCaptionCandidate> images)
    {
        var macroName = macro.GetAttributeValue("ac:name", "");
        if (macroName is not ("drawio" or "gliffy"))
        {
            // Unknown macro: not a diagram we recognize, but its parameters/body may still hold
            // renderable text -- recurse rather than dropping it.
            ConvertChildren(macro, sb, images);
            return;
        }

        var diagramName = FindMacroParameter(macro, "diagramName") ?? FindMacroParameter(macro, "name") ?? macroName;
        images.Add(new ImageCaptionCandidate(ImageSourceKind.Attachment, diagramName, diagramName));
        sb.Append(FormatImageToken(ImageSourceKind.Attachment, diagramName, diagramName));
    }

    private static string? FindMacroParameter(HtmlNode macro, string paramName)
    {
        var param = macro.Descendants("ac:parameter")
            .FirstOrDefault(p => string.Equals(p.GetAttributeValue("ac:name", ""), paramName, StringComparison.OrdinalIgnoreCase));
        if (param is null) return null;
        var text = WebUtility.HtmlDecode(param.InnerText).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // HtmlAgilityPack has no XML-namespace awareness in HTML mode -- "ri:attachment" etc. are just
    // literal (colon-containing) tag names, so a plain name-matching descendant search works and
    // avoids XPath, which treats ":" as a namespace-prefix separator and throws without a resolver.
    private static HtmlNode? FindDescendant(HtmlNode node, string name) =>
        node.Descendants().FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
}
