using Homeostat.Conditions;
using Homeostat.Diagnostics;
using Homeostat.Guardrails;
using Homeostat.Persistence;
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

    public HomeostatControl(
        ConditionSet conditions,
        IHomeostatStore store,
        MaintenanceWindows maintenance,
        TimeProvider timeProvider,
        IOptions<HomeostatOptions> options,
        Diagnostics.ActivationLog activations,
        Diagnostics.ConditionHistory history,
        Engine.SharedControl shared,
        Engine.PlanStateCache planStates)
    {
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

    public bool IsPaused => PauseReason is not null;

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

    public ValueTask PauseAsync(DateTimeOffset? until = null, string? by = null, CancellationToken cancellationToken = default)
    {
        var pause = new PauseState { Until = until, By = by, At = _timeProvider.GetUtcNow() };
        return new ValueTask(_shared.ChangeAsync(
            (store, ct) => store.SetPauseAsync(pause, ct), s => Engine.SharedControl.WithPause(s, pause), cancellationToken));
    }

    public ValueTask ResumeAsync(CancellationToken cancellationToken = default)
        => new(_shared.ChangeAsync((store, ct) => store.SetPauseAsync(null, ct), s => Engine.SharedControl.WithPause(s, null), cancellationToken));

    public async ValueTask<bool> ReleaseHoldAsync(string condition, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        if (_conditions.Get(condition)?.Definition.Plan is not { } plan)
        {
            return false;
        }

        var key = plan.Scope == Healing.PlanScope.Cluster ? plan.ConditionName : $"{_options.InstanceId}/{plan.ConditionName}";
        await _store.UpdatePlanStateAsync(key, new PlanStateUpdate { Held = false, Failures = FailureCountChange.Reset }, cancellationToken).ConfigureAwait(false);
        _planStates.Forget(key);
        return true;
    }

    public IReadOnlyList<ConditionSnapshot> GetConditions()
        => _conditions.Trackers.Select(_conditions.Snapshot).ToArray();

    public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(CancellationToken cancellationToken = default)
        => _store.GetRecentRunsAsync(_options.RunHistorySize, cancellationToken);

    public async ValueTask<ActionBudgetStatus> GetBudgetAsync(CancellationToken cancellationToken = default)
    {
        var used = await _store.GetBudgetUsedAsync(_timeProvider.GetUtcNow(), _options.ActionBudgetWindow, cancellationToken).ConfigureAwait(false);
        return new ActionBudgetStatus(used, _options.MaxActionsPerWindow, _options.ActionBudgetWindow);
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
            var local = _activations.Since(condition, since).Count(at => !mine.Any(e => Diagnostics.ConditionHistory.Same(e,
                new ConditionEventRecord { ConditionName = condition, Kind = ConditionEventKind.Activated, At = at, Instance = _options.InstanceId })));
            return mine.Count + local;
        }

        var conditions = new List<ConditionCoverage>();
        foreach (var tracker in _conditions.Trackers)
        {
            var definition = tracker.Definition;
            var mine = runs.Where(r => string.Equals(r.ConditionName, definition.Name, StringComparison.Ordinal)).ToList();
            var tried = mine.SelectMany(r => r.Steps).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            var ran = mine.SelectMany(r => r.Steps).Where(s => s.Succeeded).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            var planSteps = definition.Plan?.Steps.Select(s => s.Name).ToList() ?? [];
            conditions.Add(new ConditionCoverage(
                definition.Name,
                definition.Plan is not null,
                Activations(definition.Name),
                mine.Count,
                mine.GroupBy(r => r.Outcome.ToString()).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
                [.. planSteps.Where(ran.Contains)],
                [.. planSteps.Where(s => tried.Contains(s) && !ran.Contains(s))],
                [.. planSteps.Where(s => !tried.Contains(s))],
                mine.Count == 0 ? null : mine.Max(r => r.CompletedAt)));
        }

        return new CoverageReport(since, _timeProvider.GetUtcNow(), conditions, truncated);
    }
}
