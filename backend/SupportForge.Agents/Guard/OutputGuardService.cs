using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SupportForge.Agents.Guard;

/// <summary>
/// Result of screening one draft. Source is "off" | "jev" | "jev-failed". Replace means the caller must send
/// <see cref="DrafterAgent.LeakFallback"/> instead of the draft (see <see cref="OutputGuardService.Apply"/>).
/// </summary>
public sealed record OutputScreening(bool Replace, IReadOnlyDictionary<string, double> Signals, IReadOnlyList<string> FiredSignals, string Source);

/// <summary>
/// Screens the final draft after <see cref="DrafterAgent.LooksLikeLeak"/> (U19, plan R27). Owns the Jev timeout,
/// the fail-open path and the log lines. It logs signal names and scores only, never draft, question or snippet text.
/// </summary>
public sealed class OutputGuardService
{
    private readonly GuardOptions _options;
    private readonly IOutputGuard? _jevGuard;
    private readonly ILogger<OutputGuardService> _logger;

    public OutputGuardService(GuardOptions options, IOutputGuard? jevGuard, ILogger<OutputGuardService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _jevGuard = jevGuard;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Shared by DrafterAgent and the streaming path in ChatController, so the two can't drift: returns the text to send.
    public static string Apply(OutputScreening screening, string draft) => screening.Replace ? DrafterAgent.LeakFallback : draft;

    public async Task<OutputScreening> ScreenAsync(AgentContext context, string draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draft);

        // Off must cost nothing: no Jev call, no span and no log.
        if (_options.Mode == GuardMode.Off || _jevGuard is null)
        {
            return new OutputScreening(false, new Dictionary<string, double>(), [], "off");
        }

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("OutputGuard", ActivityKind.Internal);

        var signals = await TryAssessAsync(context, draft, ct);
        if (signals is null)
        {
            // Fail-open. LooksLikeLeak has already run on this draft, and the other defences stay in place.
            return new OutputScreening(false, new Dictionary<string, double>(), [], "jev-failed");
        }

        var fired = new List<string>();
        foreach (var name in signals.Keys.Order(StringComparer.Ordinal))
        {
            if (signals[name] >= ThresholdFor(name)) fired.Add(name);
        }

        var replace = _options.Mode == GuardMode.Enforce && fired.Count > 0;

        if (fired.Count > 0)
        {
            activity?.SetTag("guard.output_fired", string.Join(",", fired));

            // Names and scores only. The draft and the question never reach the logger.
            var described = DescribeSignals(fired, signals);
            if (replace)
            {
                _logger.LogWarning("OutputGuardTriggered: mode=Enforce project={ProjectId} signals={Signals}", context.ProjectId, described);
            }
            else
            {
                _logger.LogInformation("OutputGuardTriggered: mode=Shadow project={ProjectId} signals={Signals}", context.ProjectId, described);
            }
        }

        return new OutputScreening(replace, signals, fired, "jev");
    }

    // Returns null when Jev gave no usable answer, or when the answer arrived after our timeout. The caller fails open.
    private async Task<IReadOnlyDictionary<string, double>?> TryAssessAsync(AgentContext context, string draft, CancellationToken ct)
    {
        using var jevCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        jevCts.CancelAfter(_options.JevTimeout);

        try
        {
            var assessed = await _jevGuard!.AssessAsync(context, draft, jevCts.Token);

            // A guard that ignores its token can still return after our timeout. That answer is too late to act on.
            if (assessed is not null && !jevCts.IsCancellationRequested)
            {
                return assessed;
            }

            LogUnscreened(context, "no-result");
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Our own timeout surfaces here as OperationCanceledException and is a failure. Only the caller's token propagates.
            LogUnscreened(context, ex.GetType().Name);
            return null;
        }
    }

    private void LogUnscreened(AgentContext context, string exceptionType)
    {
        _logger.LogWarning("OutputGuardUnscreened: project={ProjectId} exception={ExceptionType}", context.ProjectId, exceptionType);
    }

    private double ThresholdFor(string name) =>
        _options.Thresholds.TryGetValue(name, out var threshold) ? threshold : _options.DefaultBlockThreshold;

    private static string DescribeSignals(IReadOnlyList<string> fired, IReadOnlyDictionary<string, double> signals) =>
        string.Join(", ", fired.Select(name => $"{name}={signals[name].ToString("0.00", CultureInfo.InvariantCulture)}"));
}
