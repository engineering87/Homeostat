using Homeostat.Conditions;
using Homeostat.Diagnostics;
using Homeostat.Guardrails;
using Homeostat.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat;

internal sealed class HomeostatControl : IHomeostatControl
{
    private readonly ConditionSet _conditions;
    private readonly IHomeostatStore _store;
    private readonly MaintenanceWindows _maintenance;
    private readonly TimeProvider _timeProvider;
    private readonly HomeostatOptions _options;
    private readonly Diagnostics.ActivationLog _activations;
    private readonly Diagnostics.ConditionHistory _history;
    private readonly Engine.SharedControl _shared;
    private readonly Engine.PlanStateCache _planStates;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    public HomeostatControl(
        ConditionSet conditions,
        IHomeostatStore store,
        MaintenanceWindows maintenance,
        TimeProvider timeProvider,
        IOptions<HomeostatOptions> options,
        Diagnostics.ActivationLog activations,
        Diagnostics.ConditionHistory history,
        Engine.SharedControl shared,
        Engine.PlanStateCache planStates,
        Microsoft.Extensions.Logging.ILogger<HomeostatControl>? logger = null)
    {
        _logger = (Microsoft.Extensions.Logging.ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _shared = shared;
        _planStates = planStates;
        _history = history;
        _activations = activations;
        _conditions = conditions;
        _store = store;
        _maintenance = maintenance;
        _timeProvider = timeProvider;
        _options = options.Value;
    }

    // Without formatting the reason: the runner reads this for every triggering signal.
    public bool IsPaused
    {
        get
        {
            if (!_shared.IsKnown)
            {
                return true;
            }

            var now = _timeProvider.GetUtcNow();
            return _shared.Current.Pause is { } pause && (pause.Until is not { } until || now < until)
                || _maintenance.IsActive(now);
        }
    }

    public string? PauseReason
    {
        get
        {
            // Not read from the store yet (it is unreachable, or the instance is starting): a pause may be in place.
            if (!_shared.IsKnown)
            {
                return "waiting for the control state from the store";
            }

            var now = _timeProvider.GetUtcNow();
            if (_shared.Current.Pause is { } pause && (pause.Until is not { } until || now < until))
            {
                var by = pause.By is { Length: > 0 } who ? $" by {who}" : string.Empty;
                return pause.Until is { } end ? $"paused until {end:O}{by}" : $"paused{by}";
            }

            return _maintenance.ActiveWindow(now) is { } window ? $"maintenance window '{window}'" : null;
        }
    }

    public bool IsDryRun => _options.DryRun;

    public async ValueTask PauseAsync(DateTimeOffset? until = null, string? by = null, CancellationToken cancellationToken = default)
    {
        // Pausing is the safe direction: when the store cannot take it, it applies here at once and is written when the
        // store answers (unless a newer pause or resume is there by then), so an outage never prevents stopping Homeostat.
        var pause = new PauseState { Until = until, By = by, At = _timeProvider.GetUtcNow() };
        if (!await _shared.ChangeOrDeferAsync(
            "pause",
            pause.At,
            (store, ct) => store.TrySetPauseAsync(pause, pause.At, ct),
            s => Engine.SharedControl.WithPause(s, pause),
            s => s.Pause is { } stored && stored.At > pause.At,
            LaterThanPause).ConfigureAwait(false))
        {
            _logger.LogWarning("Homeostat paused on this instance; the store will take the pause when it answers.");
        }
    }

    // Who paused again after a resume timed out while this instance showed no pause.
    internal const string TimedOutResume = "Homeostat (a resume that timed out)";

    // A pause refused by the store: a later pause leaves Homeostat paused (nothing lost); a later resume does not.
    private static string? LaterThanPause(ControlState stored)
        => stored.Pause is null ? "Homeostat resumed by a decision taken after this pause" : null;

    public async ValueTask ResumeAsync(string? by = null, CancellationToken cancellationToken = default)
    {
        // Resuming lets plans act again: it must reach the store, or it does not happen.
        // In decision order: a pause decided after this resume (on another instance, or written late) stands.
        var decidedAt = _timeProvider.GetUtcNow();
        var before = _shared.Current.Pause;
        bool applied;
        try
        {
            applied = await _shared.ChangeAsync(
                "pause",
                decidedAt,
                (store, ct) => store.TrySetPauseAsync(null, decidedAt, ct),
                s => Engine.SharedControl.WithPause(s, null),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // The caller is told it failed, but the store may still run the write later: the pause is decided again,
            // after it, so the store ends paused, as the caller was told. Whatever this instance's view showed: it may be
            // behind the store (a pause made elsewhere within the refresh interval, or never read), so a view without a
            // pause is no proof that the store holds none. Then the pause lasts until a person resumes (fail closed).
            var again = before is not null
                ? new PauseState { Until = before.Until, By = before.By, At = _timeProvider.GetUtcNow() }
                : new PauseState { By = TimedOutResume, At = _timeProvider.GetUtcNow() };
            await _shared.ChangeOrDeferAsync(
                "pause",
                again.At,
                (store, ct) => store.TrySetPauseAsync(again, again.At, ct),
                s => Engine.SharedControl.WithPause(s, again),
                s => s.Pause is { } stored && stored.At > again.At,
                LaterThanPause).ConfigureAwait(false);
            throw;
        }

        if (!applied)
        {
            _logger.LogWarning("Homeostat was not resumed: a later pause is in the store.");
            return;
        }

        _logger.LogInformation("Homeostat resumed{By}.", by is { Length: > 0 } who ? $" by {who.ReplaceLineEndings(" ")}" : string.Empty);
    }

    public async ValueTask<bool> ReleaseHoldAsync(string condition, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        if (_conditions.Get(condition)?.Definition.Plan is not { } plan)
        {
            return false;
        }

        var key = plan.Scope == Healing.PlanScope.Cluster ? plan.ConditionName : $"{_options.InstanceId}/{plan.ConditionName}";
        var update = new PlanStateUpdate { Held = false, Failures = FailureCountChange.Reset };
        await Engine.SharedControl.Bounded(() => _store.UpdatePlanStateAsync(key, update, cancellationToken), Engine.SharedControl.WriteTimeout, cancellationToken).ConfigureAwait(false);
        _planStates.Forget(key);
        return true;
    }

    public IReadOnlyList<ConditionSnapshot> GetConditions()
        => _conditions.Trackers.Select(_conditions.Snapshot).ToArray();

    public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(CancellationToken cancellationToken = default)
        => new(Engine.SharedControl.Bounded(() => _store.GetRecentRunsAsync(_options.RunHistorySize, cancellationToken), Engine.SharedControl.ReadTimeout, cancellationToken));

    public async ValueTask<ActionBudgetStatus> GetBudgetAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var used = await Engine.SharedControl.Bounded(() => _store.GetBudgetUsedAsync(now, _options.ActionBudgetWindow, cancellationToken), Engine.SharedControl.ReadTimeout, cancellationToken).ConfigureAwait(false);
        return new ActionBudgetStatus { Used = used, Limit = _options.MaxActionsPerWindow, Window = _options.ActionBudgetWindow };
    }

    public ValueTask<IReadOnlyList<ConditionEventRecord>> GetConditionHistoryAsync(CancellationToken cancellationToken = default)
        => _history.ReadAsync(cancellationToken);

    public async ValueTask<CoverageReport> GetCoverageAsync(DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var history = await GetRecentRunsAsync(cancellationToken).ConfigureAwait(false);
        var runs = history.Where(r => r.CompletedAt >= since).ToList();

        // A full history whose oldest entry is inside the period has lost the older entries of the period.
        var truncated = history.Count >= _options.RunHistorySize && history.Count > 0 && history.Min(r => r.CompletedAt) > since;

        // Activations: from the condition history (with the built-in stores, every instance and across restarts,
        // merged with this process's events not stored yet), plus this process's activations that the history no
        // longer holds (it keeps ConditionHistorySize events).
        var events = await _history.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (events.Count >= _options.ConditionHistorySize && events.Count > 0 && events.Min(e => e.At) > since)
        {
            truncated = true;
        }

        var stored = events.Where(e => e.Kind == ConditionEventKind.Activated && e.At >= since).ToList();
        int Activations(string condition)
        {
            var mine = stored.Where(e => string.Equals(e.ConditionName, condition, StringComparison.Ordinal)).ToList();

            // This process's activations the history does not hold, matched at the millisecond (a store may round to it).
            var held = mine.Where(e => string.Equals(e.Instance, _options.InstanceId, StringComparison.Ordinal))
                .Select(e => e.At.UtcTicks / TimeSpan.TicksPerMillisecond).ToHashSet();
            var local = _activations.Since(condition, since).Count(at =>
            {
                var ms = at.UtcTicks / TimeSpan.TicksPerMillisecond;
                return !held.Contains(ms) && !held.Contains(ms - 1) && !held.Contains(ms + 1);
            });
            return mine.Count + local;
        }

        var conditions = new List<ConditionCoverage>();
        foreach (var tracker in _conditions.Trackers)
        {
            var definition = tracker.Definition;
            var mine = runs.Where(r => string.Equals(r.ConditionName, definition.Name, StringComparison.Ordinal)).ToList();
            // Records come from the store: a hand-edited file or a custom store may leave the steps out.
            var recorded = mine.SelectMany(r => r.Steps ?? []).Where(s => s?.Name is not null).ToList();
            var tried = recorded.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            var ran = recorded.Where(s => s.Succeeded).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            var planSteps = definition.Plan?.Steps.Select(s => s.Name).ToList() ?? [];
            conditions.Add(new ConditionCoverage
            {
                Name = definition.Name,
                HasPlan = definition.Plan is not null,
                Activations = Activations(definition.Name),
                Runs = mine.Count,
                Outcomes = mine.GroupBy(r => r.Outcome.ToString()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                StepsRun = [.. planSteps.Where(ran.Contains)],
                StepsFailed = [.. planSteps.Where(s => tried.Contains(s) && !ran.Contains(s))],
                StepsNeverRun = [.. planSteps.Where(s => !tried.Contains(s))],
                LastRunAt = mine.Count == 0 ? null : mine.Max(r => r.CompletedAt),
            });
        }

        return new CoverageReport { Since = since, Until = _timeProvider.GetUtcNow(), Conditions = conditions, Truncated = truncated };
    }
}
