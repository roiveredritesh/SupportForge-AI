using System.Text;
using Moq;
using SupportForge.Agents;
using SupportForge.Core;
using SupportForge.Core.Entities;
using SupportForge.Ingestion.Documents;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class DocumentIngestionJobPdfTests
{
    [Fact]
    public async Task RunAsync_ExtractsTextFromPdf_AndEmbedsIt()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "doc.pdf"), BuildMinimalPdf("Hello PDF"));

            var llm = new Mock<ILlmClient>();
            llm.Setup(l => l.EmbedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new float[] { 0.1f });
            var vectorStore = new Mock<IVectorStoreService>();
            List<VectorDocument>? upserted = null;
            vectorStore.Setup(v => v.UpsertAsync("proj1-kb", It.IsAny<IReadOnlyList<VectorDocument>>(), It.IsAny<CancellationToken>()))
                .Callback<string, IReadOnlyList<VectorDocument>, CancellationToken>((_, docs, _) => upserted = docs.ToList())
                .Returns(Task.CompletedTask);
            var projects = new Mock<IProjectRepository>();
            projects.Setup(p => p.GetByIdAsync("proj1", It.IsAny<CancellationToken>()))
                .ReturnsAsync((Project?)null);

            var job = new DocumentIngestionJob("proj1", folder, "docs/", llm.Object, vectorStore.Object, projects.Object);

            await job.RunAsync(CancellationToken.None);

            Assert.NotNull(upserted);
            Assert.Single(upserted!);
            Assert.Contains("Hello PDF", upserted![0].Text);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_ThrowsInvalidOperationException_WhenPdfIsCorrupt()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "doc.pdf"), Encoding.ASCII.GetBytes("not a real pdf"));

            var job = new DocumentIngestionJob(
                "proj1", folder, "docs/",
                new Mock<ILlmClient>().Object,
                new Mock<IVectorStoreService>().Object,
                new Mock<IProjectRepository>().Object);

            await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Hand-built minimal single-page PDF containing one text run, with byte-accurate xref offsets.
    private static byte[] BuildMinimalPdf(string text)
    {
        var objects = new List<string>
        {
            "<</Type/Catalog/Pages 2 0 R>>",
            "<</Type/Pages/Kids[3 0 R]/Count 1>>",
            "<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
            null!, // placeholder for the content stream, built below
            "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>",
        };

        var content = $"BT /F1 24 Tf 10 100 Td ({text}) Tj ET";
        objects[3] = $"<</Length {content.Length}>>\nstream\n{content}\nendstream";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        var offsets = new int[objects.Count + 1];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = Encoding.ASCII.GetByteCount(sb.ToString());
            sb.Append($"{i + 1} 0 obj{objects[i]}\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objects.Count + 1}\n");
        sb.Append("0000000000 65535 f \n");
        for (var i = 1; i <= objects.Count; i++)
            sb.Append($"{offsets[i]:D10} 00000 n \n");

        sb.Append($"trailer<</Size {objects.Count + 1}/Root 1 0 R>>\n");
        sb.Append($"startxref\n{xrefOffset}\n%%EOF");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
