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

    // GraphifyHealthCheck shells out to a real `graphify` process, whose presence on PATH is
    // environment-dependent (absent in most CI/test environments). This asserts the contract that
    // matters regardless of environment: the check reports a status, it never throws -- whether
    // that status is Healthy or Unhealthy depends on whether graphify happens to be installed here.
    [Fact]
    public async Task GraphifyHealthCheck_NeverThrows_RegardlessOfBinaryAvailability()
    {
        var check = new GraphifyHealthCheck();

        var result = await check.CheckHealthAsync(Context);

        Assert.True(result.Status is HealthStatus.Healthy or HealthStatus.Unhealthy);
    }
}
