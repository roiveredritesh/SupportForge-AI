using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using SupportForge.Agents;
using SupportForge.Api.HealthChecks;
using SupportForge.VectorStore;
using SupportForge.VectorStore.Models;
using Xunit;

namespace SupportForge.Api.Tests.HealthChecks;

public class HealthCheckTests
{
    private static readonly HealthCheckContext Context = new();

    [Fact]
    public async Task VectorStoreHealthCheck_WhenQuerySucceeds_ReturnsHealthy()
    {
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore
            .Setup(v => v.QueryAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VectorQueryResult>());

        var check = new VectorStoreHealthCheck(vectorStore.Object);
        var result = await check.CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task VectorStoreHealthCheck_WhenQueryThrows_ReturnsUnhealthy()
    {
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore
            .Setup(v => v.QueryAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var check = new VectorStoreHealthCheck(vectorStore.Object);
        var result = await check.CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task VectorStoreHealthCheck_DoesNotThrow_OnInternalFailure()
    {
        var vectorStore = new Mock<IVectorStoreService>();
        vectorStore
            .Setup(v => v.QueryAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var check = new VectorStoreHealthCheck(vectorStore.Object);
        var result = await check.CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task LlmConnectivityHealthCheck_WhenClientResolved_ReturnsHealthy()
    {
        var llm = new Mock<ILlmChatClient>();
        var check = new LlmConnectivityHealthCheck(llm.Object);

        var result = await check.CheckHealthAsync(Context);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }
}
