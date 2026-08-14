using SupportForge.Ingestion.Documents;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class DocumentTextExtractorTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Ingestion", "Fixtures", name);

    [Fact]
    public async Task ExtractAsync_Pdf_ReturnsNonEmptyText()
    {
        var text = await DocumentTextExtractor.ExtractAsync(FixturePath("sample.pdf"), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("Hello PDF extraction test", text);
    }

    [Fact]
    public async Task ExtractAsync_Docx_ExtractsParagraphAndTableAsMarkdown()
    {
        var text = await DocumentTextExtractor.ExtractAsync(FixturePath("sample.docx"), CancellationToken.None);

        Assert.Contains("This is a sample paragraph for extraction testing.", text);
        Assert.Contains("| Header1 | Header2 |", text);
        Assert.Contains("| --- | --- |", text);
        Assert.Contains("| CellA | CellB |", text);
    }

    [Fact]
    public async Task ExtractAsync_Pptx_ExtractsTextFromBothSlides()
    {
        var text = await DocumentTextExtractor.ExtractAsync(FixturePath("sample.pptx"), CancellationToken.None);

        Assert.Contains("Slide one content.", text);
        Assert.Contains("Slide two content.", text);
    }

    [Fact]
    public async Task ExtractAsync_CorruptPdf_Throws()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var corruptPath = Path.Combine(folder, "corrupt.pdf");
            await File.WriteAllTextAsync(corruptPath, "not a real pdf");

            await Assert.ThrowsAnyAsync<Exception>(() => DocumentTextExtractor.ExtractAsync(corruptPath, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
