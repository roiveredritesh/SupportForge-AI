using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Wordprocessing;

namespace SupportForge.Ingestion.Documents;

/// <summary>
/// U10: extracts plain/Markdown text from .pdf, .docx, .pptx files so they can flow through the same
/// KbVectorIndexer chunk-embed-upsert path as .md/.txt. PDF extraction is raw page text only -- no
/// table-structure reconstruction (KTD5's documented ceiling; same fidelity as copy-pasting from a
/// PDF viewer). DOCX tables are converted to Markdown table syntax via a small local helper --
/// duplicated rather than shared with U1's HtmlToMarkdownConverter per the plan's own hedge.
/// </summary>
public static class DocumentTextExtractor
{
    public static Task<string> ExtractAsync(string filePath, CancellationToken ct) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".pdf" => Task.FromResult(ExtractPdf(filePath)),
            ".docx" => Task.FromResult(ExtractDocx(filePath)),
            ".pptx" => Task.FromResult(ExtractPptx(filePath)),
            var ext => throw new NotSupportedException($"Unsupported extension '{ext}' for text extraction."),
        };

    private static string ExtractPdf(string filePath)
    {
        using var docReader = DocLib.Instance.GetDocReader(filePath, new PageDimensions(1080, 1920));
        var pages = new List<string>();
        for (var i = 0; i < docReader.GetPageCount(); i++)
        {
            using var pageReader = docReader.GetPageReader(i);
            pages.Add(pageReader.GetText());
        }
        return string.Join("\n\n", pages);
    }

    private static string ExtractDocx(string filePath)
    {
        using var doc = WordprocessingDocument.Open(filePath, false);
        var body = doc.MainDocumentPart?.Document.Body;
        if (body is null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var element in body.Elements())
        {
            switch (element)
            {
                case Paragraph p:
                    var text = p.InnerText;
                    if (!string.IsNullOrWhiteSpace(text)) sb.AppendLine(text);
                    break;
                case Table table:
                    sb.AppendLine(TableToMarkdown(table));
                    break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    // Duplicates U1's HtmlToMarkdownConverter table-row formatting (pipe-delimited, header + separator
    // row) -- intentionally not shared, per the plan's hedge on this point.
    private static string TableToMarkdown(Table table)
    {
        var rows = table.Elements<TableRow>().ToList();
        if (rows.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].Elements<TableCell>().Select(c => c.InnerText.Trim());
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
            if (r == 0)
            {
                var colCount = rows[r].Elements<TableCell>().Count();
                sb.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", colCount)));
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string ExtractPptx(string filePath)
    {
        using var doc = PresentationDocument.Open(filePath, false);
        var presentationPart = doc.PresentationPart;
        var slideIds = presentationPart?.Presentation.SlideIdList?.Elements<SlideId>() ?? [];

        var slides = new List<string>();
        foreach (var slideId in slideIds)
        {
            var relId = slideId.RelationshipId!;
            var slidePart = (SlidePart)presentationPart!.GetPartById(relId!);
            var texts = slidePart.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>().Select(t => t.Text);
            slides.Add(string.Join(" ", texts));
        }
        return string.Join("\n\n", slides);
    }
}
