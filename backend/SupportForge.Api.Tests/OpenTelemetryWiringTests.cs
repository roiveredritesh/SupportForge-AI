using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace SupportForge.Api.Tests;

// C5 (gap-closing-solutions.md Phase C, item 5): confirms WithMetrics() actually registered a
// MeterProvider (the gap being closed -- tracing already had a TracerProvider) rather than just
// compiling without error. Program.cs wiring, not business logic, so this is a smoke check, not a
// behavioral test of what gets exported.
public class OpenTelemetryWiringTests
{
    [Fact]
    public async Task App_RegistersBothTracerProviderAndMeterProvider()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<TracerProvider>());
        Assert.NotNull(scope.ServiceProvider.GetService<MeterProvider>());
    }
}
