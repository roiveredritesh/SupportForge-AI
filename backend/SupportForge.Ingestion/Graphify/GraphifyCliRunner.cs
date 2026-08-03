using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SupportForge.Ingestion.Graphify;

/// <summary>
/// Single chokepoint for invoking the installed `graphify` CLI as a subprocess. Every later
/// ingestion/query path (code extraction, doc extraction, refresh, RCA traversal) goes through
/// this runner instead of each call site re-implementing process handling.
/// </summary>
public sealed class GraphifyCliRunner
{
    private readonly SemaphoreSlim _concurrencyGate;
    private readonly ILogger<GraphifyCliRunner> _logger;
    private readonly TimeSpan _timeout;

    public GraphifyCliRunner(ILogger<GraphifyCliRunner> logger, TimeSpan? timeout = null, int concurrency = 2)
    {
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        _concurrencyGate = new SemaphoreSlim(concurrency, concurrency);
    }

    /// <summary>
    /// Runs `graphify &lt;args&gt;` in <paramref name="workingDirectory"/>. Arguments are passed via
    /// an OS argument array (never a shell command string), so no argument requires escaping and
    /// none can be interpreted as a shell command -- ticket text, URLs, and other external-input
    /// values are still validated (see <see cref="ValidateArgument"/>) as defense in depth against
    /// argument-injection into the CLI's own flag parser.
    /// </summary>
    public async Task<string> RunAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken ct,
        params string[] args)
    {
        foreach (var arg in args)
            ValidateArgument(arg);

        await _concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunProcessAsync(workingDirectory, environment, args, ct).ConfigureAwait(false);
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    private async Task<string> RunProcessAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        string[] args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo("graphify")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

        using var process = new Process { StartInfo = psi };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Kill regardless of which token fired: leaving the child running on caller
            // cancellation (not just on our own timeout) orphans a graphify process that keeps
            // consuming CPU/LLM budget after the caller has already moved on.
            TryKill(process);
            if (!ct.IsCancellationRequested)
                throw new TimeoutException($"graphify {string.Join(' ', args)} exceeded the configured timeout of {_timeout}.");
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        _logger.LogDebug(
            "graphify {Args} exited {ExitCode}. stdout length: {StdoutLength}, stderr length: {StderrLength}",
            string.Join(' ', args), process.ExitCode, stdout.Length, stderr.Length);

        if (process.ExitCode != 0)
            throw new GraphifyCliException(args, process.ExitCode, Redact(stderr, environment));

        return stdout;
    }

    /// <summary>
    /// Scrubs secret-shaped environment values (keys containing KEY/TOKEN/SECRET) out of captured
    /// stderr before it lands in an exception message -- and from there, in application logs via
    /// the standard `_logger.LogError(exception, ...)` pattern callers use. graphify's subprocess
    /// environment can carry live provider API keys (see GraphifyBackendResolver), and a
    /// misconfigured or failing provider can echo them back on stderr.
    /// </summary>
    internal static string Redact(string text, IReadOnlyDictionary<string, string?>? environment)
    {
        if (environment is null) return text;
        foreach (var (key, value) in environment)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (!key.Contains("KEY", StringComparison.OrdinalIgnoreCase)
                && !key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                && !key.Contains("SECRET", StringComparison.OrdinalIgnoreCase))
                continue;
            text = text.Replace(value, "***REDACTED***");
        }
        return text;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort: the process may have exited between the check and the kill.
        }
    }

    /// <summary>
    /// Rejects control characters that have no legitimate place in a CLI argument (null bytes,
    /// embedded newlines) regardless of source. Argument-array invocation already prevents shell
    /// interpretation; this guards against argument-injection into graphify's own flag parser for
    /// values sourced from tickets, URLs, or other external input.
    /// </summary>
    private static void ValidateArgument(string arg)
    {
        if (arg.Contains('\0'))
            throw new ArgumentException("graphify CLI argument may not contain a null byte.", nameof(arg));
        if (arg.Contains('\n') || arg.Contains('\r'))
            throw new ArgumentException("graphify CLI argument may not contain embedded newlines.", nameof(arg));
    }
}

public sealed class GraphifyCliException : Exception
{
    public string[] Args { get; }
    public int ExitCode { get; }

    public GraphifyCliException(string[] args, int exitCode, string stderr)
        : base($"graphify {string.Join(' ', args)} exited with code {exitCode}: {stderr}")
    {
        Args = args;
        ExitCode = exitCode;
    }
}
