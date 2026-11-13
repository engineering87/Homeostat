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
    private readonly HealingGroups _groups;

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
        HealingGroups groups,
        Microsoft.Extensions.Logging.ILogger<HomeostatControl>? logger = null)
    {
        _groups = groups;
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
            return InForce(_shared.Current.Pause, now) || _maintenance.IsActive(now);
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
            if (_shared.Current.Pause is { } pause && InForce(pause, now))
            {
                return Describe(pause);
            }

            return _maintenance.ActiveWindow(now) is { } window ? $"maintenance window '{window}'" : null;
        }
    }

    // Also read for every triggering signal of a condition in a group: no formatting, a lookup in the view.
    public bool IsGroupPaused(string group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return IsPaused || IsOwnGroupPause(group, _timeProvider.GetUtcNow());
    }

    public string? GetGroupPauseReason(string group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return PauseReason ?? OwnGroupPauseReason(group, _timeProvider.GetUtcNow());
    }

    // The group's own pause: by a person, or by one of its maintenance windows (not the global pause).
    private bool IsOwnGroupPause(string group, DateTimeOffset now)
        => InForce(_shared.GroupPause(group), now) || _maintenance.ActiveGroupWindow(now, group) is not null;

    private string? OwnGroupPauseReason(string group, DateTimeOffset now)
    {
        if (_shared.GroupPause(group) is { } pause && InForce(pause, now))
        {
            return Describe(pause.ToPause());
        }

        return _maintenance.ActiveGroupWindow(now, group) is { } window ? $"maintenance window '{window}'" : null;
    }

    private static bool InForce(PauseState? pause, DateTimeOffset now) => pause is not null && (pause.Until is not { } until || now < until);

    private static bool InForce(GroupPauseState? pause, DateTimeOffset now) => pause is not null && (pause.Until is not { } until || now < until);

    private static string Describe(PauseState pause)
    {
        var by = pause.By is { Length: > 0 } who ? $" by {who}" : string.Empty;
        return pause.Until is { } end ? $"paused until {end:O}{by}" : $"paused{by}";
    }

    public bool IsDryRun => _options.DryRun;

    public ValueTask PauseAsync(DateTimeOffset? until = null, string? by = null, CancellationToken cancellationToken = default)
        => PauseCoreAsync(PauseTarget.Global, until, by);

    public ValueTask ResumeAsync(string? by = null, CancellationToken cancellationToken = default)
        => ResumeCoreAsync(PauseTarget.Global, by, cancellationToken);

    public ValueTask PauseGroupAsync(string group, DateTimeOffset? until = null, string? by = null, CancellationToken cancellationToken = default)
    {
        var target = PauseTarget.Of(Guardrails.HealingGroupNames.Validate(group, nameof(group)));

        // Bounded memory (decision F7): a pause of one group too many is refused here, before it applies anywhere
        // (thrown at once, so callers tell it from a store failure). Replacing the pause of a group already paused is
        // always allowed. Counted on what this instance knows: before the state is first read, the pauses of groups
        // still waiting to be written count, so a store down at startup does not let pending pauses grow without end.
        // The store checks again (this instance's view may be behind it): see DecidePauseAsync.
        if (GroupPauseLimit(_shared.Known, group) is { } refused)
        {
            throw new InvalidOperationException(refused);
        }

        // An unknown name is not refused (decision G4: the group may come from configuration or from another
        // instance), but a mistyped name would pause nothing: say so.
        if (!_groups.Configured.Contains(group, StringComparer.Ordinal)
            && !_conditions.Trackers.Any(t => string.Equals(t.Definition.Group, group, StringComparison.Ordinal)))
        {
            _logger.LogWarning("Healing group {Group} is paused, but no condition or ConfigureGroup on this instance uses it: check the name.", group);
        }

        return PauseCoreAsync(target, until, by, s => GroupPauseLimit(s, group));
    }

    // Why a state does not admit one more paused group, or null (the group is paused already, or there is room).
    private static string? GroupPauseLimit(ControlState state, string group)
        => Engine.SharedControl.GroupPauseOf(state, group) is null
            && state.GroupPauses.Select(p => p.Group).Distinct(StringComparer.Ordinal).Count() >= ControlState.MaxGroupPauses
            ? $"At most {ControlState.MaxGroupPauses} healing groups can be paused at once: resume groups that no longer need their pause."
            : null;

    public ValueTask ResumeGroupAsync(string group, string? by = null, CancellationToken cancellationToken = default)
        => ResumeCoreAsync(PauseTarget.Of(Guardrails.HealingGroupNames.Validate(group, nameof(group))), by, cancellationToken);

    // Who paused again after a resume timed out while this instance showed no pause.
    internal const string TimedOutResume = "Homeostat (a resume that timed out)";

    /// <summary>
    /// What a pause or a resume is about: the global pause, or the pause of one group. Both follow the same path (the
    /// same deferral, decision order, read-backs and fail-closed rules), so the decisions S1 to S9 hold for groups by
    /// construction.
    /// </summary>
    private sealed class PauseTarget
    {
        public static readonly PauseTarget Global = new(null);

        private PauseTarget(string? group)
        {
            Group = group;
            Key = group is null ? "pause" : $"group-pause:{group}";
        }

        public static PauseTarget Of(string group) => new(group);

        public string? Group { get; }

        /// <summary>What the change is about in the shared control (one pending change per key).</summary>
        public string Key { get; }

        public PauseState? Read(ControlState state)
            => Group is null ? state.Pause : Engine.SharedControl.GroupPauseOf(state, Group);

        public ControlState With(ControlState state, PauseState? pause)
            => Group is null ? Engine.SharedControl.WithPause(state, pause) : Engine.SharedControl.WithGroupPause(state, Group, pause);

        public ValueTask<bool> WriteAsync(IHomeostatStore store, PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken)
            => Group is null ? store.TrySetPauseAsync(pause, decidedAt, cancellationToken) : store.TrySetGroupPauseAsync(Group, pause, decidedAt, cancellationToken);

        // A pause refused by the store: a later pause leaves the target paused (nothing lost); a later resume does not.
        public string? LaterThanPause(ControlState stored)
            => Read(stored) is not null ? null
                : Group is null ? "Homeostat resumed by a decision taken after this pause"
                : $"healing group '{Group}' resumed by a decision taken after this pause";
    }

    private async ValueTask PauseCoreAsync(PauseTarget target, DateTimeOffset? until, string? by, Func<ControlState, string?>? limit = null)
    {
        // Pausing is the safe direction: when the store cannot take it, it applies here at once and is written when the
        // store answers (unless a newer pause or resume is there by then), so an outage never prevents stopping Homeostat.
        var pause = new PauseState { Until = until, By = by, At = _timeProvider.GetUtcNow() };
        if (!await DecidePauseAsync(target, pause, limit).ConfigureAwait(false))
        {
            if (target.Group is null)
            {
                _logger.LogWarning("Homeostat paused on this instance; the store will take the pause when it answers.");
            }
            else
            {
                _logger.LogWarning("Healing group {Group} paused on this instance; the store will take the pause when it answers.", target.Group);
            }
        }
    }

    // With a limit (a person's pause of a group): a pause the store refuses for its limit (InvalidOperationException,
    // confirmed by a read) is not deferred but dropped, and the caller gets the InvalidOperationException, as when this
    // instance's view already showed the limit. Without one (the pause decided again after a resume that timed out, S4),
    // any store failure defers it: it is retried until the store takes it (fail closed).
    private Task<bool> DecidePauseAsync(PauseTarget target, PauseState pause, Func<ControlState, string?>? limit = null)
        => _shared.ChangeOrDeferAsync(
            target.Key,
            pause.At,
            (store, ct) => target.WriteAsync(store, pause, pause.At, ct),
            s => target.With(s, pause),
            s => target.Read(s) is { } stored && stored.At > pause.At,
            target.LaterThanPause,
            limit);

    private async ValueTask ResumeCoreAsync(PauseTarget target, string? by, CancellationToken cancellationToken)
    {
        // Resuming lets plans act again: it must reach the store, or it does not happen.
        // In decision order: a pause decided after this resume (on another instance, or written late) stands.
        var decidedAt = _timeProvider.GetUtcNow();
        var before = target.Read(_shared.Current);
        bool applied;
        try
        {
            applied = await _shared.ChangeAsync(
                target.Key,
                decidedAt,
                (store, ct) => target.WriteAsync(store, null, decidedAt, ct),
                s => target.With(s, null),
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
            await DecidePauseAsync(target, again).ConfigureAwait(false);
            throw;
        }

        var who = by is { Length: > 0 } name ? $" by {name.ReplaceLineEndings(" ")}" : string.Empty;
        if (target.Group is null)
        {
            if (!applied)
            {
                _logger.LogWarning("Homeostat was not resumed: a later pause is in the store.");
                return;
            }

            _logger.LogInformation("Homeostat resumed{By}.", who);
            return;
        }

        if (!applied)
        {
            _logger.LogWarning("Healing group {Group} was not resumed: a later pause of the group is in the store.", target.Group);
            return;
        }

        _logger.LogInformation("Healing group {Group} resumed{By}.", target.Group, who);
    }

    public IReadOnlyList<Guardrails.HealingGroupStatus> GetGroups()
    {
        var now = _timeProvider.GetUtcNow();
        var conditions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var tracker in _conditions.Trackers)
        {
            if (tracker.Definition.Group is { } group)
            {
                (conditions.TryGetValue(group, out var list) ? list : conditions[group] = []).Add(tracker.Definition.Name);
            }
        }

        var names = conditions.Keys
            .Concat(_groups.Configured)
            .Concat(_shared.Current.GroupPauses.Select(p => p.Group))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        return names.Select(name =>
        {
            var guardrails = _groups.Get(name);
            return new Guardrails.HealingGroupStatus
            {
                Name = name,
                Conditions = conditions.TryGetValue(name, out var members) ? members : [],
                IsPaused = !_shared.IsKnown || IsOwnGroupPause(name, now),
                PauseReason = _shared.IsKnown ? OwnGroupPauseReason(name, now) : "waiting for the control state from the store",
                Pause = _shared.GroupPause(name),
                MaxConcurrentHealings = guardrails?.MaxConcurrentHealings,
                MaxActionsPerWindow = guardrails?.MaxActionsPerWindow,
                ActionBudgetWindow = guardrails?.MaxActionsPerWindow is null ? null : guardrails.ActionBudgetWindow,
            };
        }).ToArray();
    }

    public async ValueTask<ActionBudgetStatus?> GetGroupBudgetAsync(string group, CancellationToken cancellationToken = default)
    {
        Guardrails.HealingGroupNames.Validate(group, nameof(group));
        if (_groups.Get(group) is not { MaxActionsPerWindow: { } limit } guardrails)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var used = await Engine.SharedControl.Bounded(() => _store.GetGroupBudgetUsedAsync(group, now, guardrails.ActionBudgetWindow, cancellationToken), Engine.SharedControl.ReadTimeout, cancellationToken).ConfigureAwait(false);
        return new ActionBudgetStatus { Group = group, Used = used, Limit = limit, Window = guardrails.ActionBudgetWindow };
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
