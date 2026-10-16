using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SupportForge.Agents.Guard;

/// <summary>
/// Screens one customer turn before any agent runs. Owns the Jev timeout, the fail-open path and the
/// audit log, and delegates the verdict to <see cref="GuardPolicy"/>.
/// </summary>
public sealed class GuardService
{
    private static readonly GuardAssessment EmptyHeuristic = new(new Dictionary<string, double>(), "heuristic");

    private readonly HeuristicInputGuard _heuristic;
    private readonly IInputGuard? _jevGuard;
    private readonly GuardRepeatTracker _repeatTracker;
    private readonly ILogger<GuardService> _logger;

    public GuardService(GuardOptions options, HeuristicInputGuard heuristic, IInputGuard? jevGuard,
                        GuardRepeatTracker repeatTracker, ILogger<GuardService> logger)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _heuristic = heuristic ?? throw new ArgumentNullException(nameof(heuristic));
        _jevGuard = jevGuard;
        _repeatTracker = repeatTracker ?? throw new ArgumentNullException(nameof(repeatTracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public GuardOptions Options { get; }

    public async Task<GuardDecision> ScreenAsync(GuardInput input, string userId, string projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Off must cost nothing: neither guard runs, and nothing is logged or recorded.
        if (Options.Mode == GuardMode.Off)
        {
            return GuardPolicy.Decide(Options, EmptyHeuristic, jev: null, jevFailed: false);
        }

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Guard", ActivityKind.Internal);

        var heuristicAssessment = await _heuristic.AssessAsync(input, ct);

        GuardAssessment? jev = null;
        var jevFailed = false;
        if (_jevGuard is not null)
        {
            jev = await TryAssessJevAsync(input, userId, projectId, ct);
            jevFailed = jev is null;
        }

        var decision = GuardPolicy.Decide(Options, heuristicAssessment, jev, jevFailed);

        activity?.SetTag("guard.verdict", decision.Verdict.ToString().ToLowerInvariant());
        activity?.SetTag("guard.source", decision.Source);
        activity?.SetTag("guard.fired", string.Join(",", decision.FiredSignals));

        // Logs carry signal names and scores only. The message text and history never reach the logger.
        switch (decision.Verdict)
        {
            case GuardVerdict.Blocked:
                _logger.LogWarning(
                    "GuardBlocked user={UserId} project={ProjectId} signals=[{Signals}] source={Source}",
                    userId, projectId, DescribeSignals(decision), decision.Source);
                if (_repeatTracker.RecordBlock(userId, Options))
                {
                    _logger.LogWarning(
                        "GuardRepeatOffender user={UserId} project={ProjectId} blocks={BlockCount} windowMinutes={WindowMinutes}",
                        userId, projectId, Options.RepeatBlockCount, Options.RepeatBlockWindow.TotalMinutes);
                }
                break;

            case GuardVerdict.Flagged:
                _logger.LogInformation(
                    "GuardFlagged user={UserId} project={ProjectId} signals=[{Signals}] source={Source}",
                    userId, projectId, DescribeSignals(decision), decision.Source);
                break;

            case GuardVerdict.Unscreened:
                _logger.LogInformation(
                    "GuardUnscreened user={UserId} project={ProjectId} signals=[{Signals}] source={Source}",
                    userId, projectId, DescribeSignals(decision), decision.Source);
                break;
        }

        return decision;
    }

    // Returns null when Jev gave no usable answer. The caller then falls back to the heuristic-only
    // policy, which fails open unless FailClosed is set.
    private async Task<GuardAssessment?> TryAssessJevAsync(GuardInput input, string userId, string projectId, CancellationToken ct)
    {
        using var jevCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        jevCts.CancelAfter(Options.JevTimeout);

        try
        {
            var assessment = await _jevGuard!.AssessAsync(TruncateHistory(input), jevCts.Token);

            // A guard that ignores its token can still return after our timeout. That answer is too
            // late to act on, so it counts as a failure, as does a null result.
            if (assessment is not null && !jevCts.IsCancellationRequested)
            {
                return assessment;
            }

            LogJevFailure(null, userId, projectId);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Our own timeout surfaces here as OperationCanceledException and is a failure, not a cancel.
            // Only the caller's token is allowed to propagate.
            LogJevFailure(ex, userId, projectId);
            return null;
        }
    }

    private void LogJevFailure(Exception? exception, string userId, string projectId)
    {
        _logger.LogWarning(exception,
            "Guard Jev check failed; falling back to {Mode} policy. user={UserId} project={ProjectId}",
            Options.Mode, userId, projectId);
    }

    private GuardInput TruncateHistory(GuardInput input)
    {
        var keep = Math.Max(0, Options.HistoryTurns);
        if (input.History is null || input.History.Count <= keep) return input;

        return input with { History = input.History.Skip(input.History.Count - keep).ToList() };
    }

    private static string DescribeSignals(GuardDecision decision) =>
        string.Join(", ", decision.FiredSignals.Select(name =>
            $"{name}={Score(decision, name).ToString("0.00", CultureInfo.InvariantCulture)}"));

    private static double Score(GuardDecision decision, string name) =>
        decision.Signals.TryGetValue(name, out var value) ? value : 0.0;
}
