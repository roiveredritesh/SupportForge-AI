using Microsoft.Extensions.Logging.Abstractions;
using SupportForge.Ingestion.Graphify;
using Xunit;

namespace SupportForge.Api.Tests.Ingestion;

public class GraphifyCliRunnerTests
{
    private static GraphifyCliRunner CreateRunner(TimeSpan? timeout = null) =>
        new(NullLogger<GraphifyCliRunner>.Instance, timeout);

    [Fact]
    public async Task RunAsync_ReturnsStdout_ForBenignSubcommand()
    {
        var runner = CreateRunner();

        var stdout = await runner.RunAsync(Directory.GetCurrentDirectory(), environment: null, CancellationToken.None, "--version");

        Assert.Contains("graphify", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ThrowsWithStderr_ForInvalidSubcommand()
    {
        var runner = CreateRunner();

        var ex = await Assert.ThrowsAsync<GraphifyCliException>(() =>
            runner.RunAsync(Directory.GetCurrentDirectory(), environment: null, CancellationToken.None, "not-a-real-subcommand"));

        Assert.NotEqual(0, ex.ExitCode);
        Assert.Contains("not-a-real-subcommand", ex.Message);
    }

    [Fact]
    public async Task RunAsync_ThrowsArgumentException_ForNullByteInArgument()
    {
        var runner = CreateRunner();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(Directory.GetCurrentDirectory(), environment: null, CancellationToken.None, "query", "bad\0arg"));
    }

    [Fact]
    public async Task RunAsync_ThrowsArgumentException_ForEmbeddedNewlineInArgument()
    {
        var runner = CreateRunner();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(Directory.GetCurrentDirectory(), environment: null, CancellationToken.None, "query", "line one\nline two"));
    }

    [Fact]
    public async Task RunAsync_HonorsCancellation_BeforeProcessStarts()
    {
        var runner = CreateRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(Directory.GetCurrentDirectory(), environment: null, cts.Token, "--version"));
    }

    [Fact]
    public async Task RunAsync_ThrowsTimeoutException_WhenExceedingConfiguredTimeout()
    {
        // A near-zero timeout guarantees the process (Python interpreter startup alone takes
        // longer than this) is still running when the timeout fires, exercising the kill path.
        var runner = CreateRunner(TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            runner.RunAsync(Directory.GetCurrentDirectory(), environment: null, CancellationToken.None, "--version"));
    }

    [Fact]
    public async Task RunAsync_ThrowsOperationCanceledException_NotTimeoutException_WhenCallerCancelsDuringExecution()
    {
        // Regression test: the kill-on-cancel branch previously only fired when the internal
        // timeout (not the caller's own token) triggered cancellation, leaving the child process
        // running whenever the caller cancelled instead. Cancelling almost immediately after start
        // (well within the default 10-minute timeout) exercises the caller-cancellation branch.
        var runner = CreateRunner();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(1));

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(Directory.GetCurrentDirectory(), environment: null, cts.Token, "--version"));

        Assert.IsNotType<TimeoutException>(ex);
    }

    [Fact]
    public async Task RunAsync_AllowsConcurrentCallsWithinTheGate_WithoutDeadlock()
    {
        var runner = CreateRunner();

        var first = runner.RunAsync(Directory.GetCurrentDirectory(), environment: null, CancellationToken.None, "--version");
        var second = runner.RunAsync(Directory.GetCurrentDirectory(), environment: null, CancellationToken.None, "--version");

        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.Contains("graphify", r, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RunAsync_PassesArgumentsLiterally_EvenWithShellMetacharacters()
    {
        // Argument-array invocation means a value like this is never shell-interpreted; graphify
        // itself rejects it as an unrecognized subcommand, proving it arrived as one literal argument
        // rather than being split/expanded by a shell.
        var runner = CreateRunner();
        const string adversarial = "; rm -rf /; echo pwned";

        var ex = await Assert.ThrowsAsync<GraphifyCliException>(() =>
            runner.RunAsync(Directory.GetCurrentDirectory(), environment: null, CancellationToken.None, adversarial));

        Assert.NotEqual(0, ex.ExitCode);
    }
}
