using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SupportForge.Core;
using SupportForge.Ingestion.Documents;
using SupportForge.Ingestion.Graphify;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class WebsiteIngestionJobTests
{
    private static WebsiteIngestionJob CreateJob(string url) =>
        new("proj1", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), url,
            new GraphifyCliRunner(NullLogger<GraphifyCliRunner>.Instance),
            new Dictionary<string, string?>(), new Mock<IProjectRepository>().Object);

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/file")]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://localhost/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://10.0.0.5/internal")]
    [InlineData("http://192.168.1.1/internal")]
    [InlineData("http://172.16.0.1/internal")]
    public async Task RunAsync_RejectsNonPublicOrNonHttpUrls(string url)
    {
        var job = CreateJob(url);

        await Assert.ThrowsAsync<ArgumentException>(() => job.RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_RejectsUrl_BeforeTouchingGraphifyOrDisk()
    {
        var corpusPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var job = new WebsiteIngestionJob("proj1", corpusPath, "http://localhost/x",
            new GraphifyCliRunner(NullLogger<GraphifyCliRunner>.Instance), new Dictionary<string, string?>(), new Mock<IProjectRepository>().Object);

        await Assert.ThrowsAsync<ArgumentException>(() => job.RunAsync(CancellationToken.None));

        Assert.False(Directory.Exists(corpusPath));
    }
}
