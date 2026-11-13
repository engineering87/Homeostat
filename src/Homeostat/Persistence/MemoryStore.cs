using Homeostat.Healing;

namespace Homeostat.Persistence;

/// <summary>
/// The in-memory implementation behind <see cref="InMemoryHomeostatStore"/> and <see cref="FileHomeostatStore"/>; the
/// file store persists it after every change.
/// </summary>
internal class MemoryStore : IHomeostatStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PlanState> _plans = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _budget = new();
    private readonly LinkedList<HealingRunRecord> _runs = new();
    private readonly LinkedList<ConditionEventRecord> _events = new();
    private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SwitchState> _switches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApprovalRecord> _approvals = new(StringComparer.Ordinal);
    private PauseState? _pause;
    private readonly Dictionary<string, PauseState> _groupPauses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _groupBudgets = new(StringComparer.Ordinal);

    // When every unit spent by a group has left its window (the latest unit plus the window of the latest call about the
    // group): from then on its budget is empty and is removed, by any budget call. Groups that are renamed or removed are
    // never asked about again, and the file store would otherwise keep their budgets forever.
    private readonly Dictionary<string, DateTimeOffset> _groupBudgetEnds = new(StringComparer.Ordinal);

    // When the latest decision about the pause, about the pause of each group and about each switch was taken: an older
    // write is refused. Kept in memory only (a late write does not outlive a restart).
    private DateTimeOffset _pauseDecidedAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, DateTimeOffset> _groupPauseDecidedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _switchDecidedAt = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_plans.TryGetValue(key, out var state) ? state : PlanState.Empty);
        }
    }

    /// <inheritdoc />
    public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var current = _plans.TryGetValue(key, out var state) ? state : PlanState.Empty;
            var next = update.ApplyTo(current);
            if (next != current)
            {
                _plans[key] = next;
                OnChanged();
            }

            return ValueTask.FromResult(next);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RemoveEndedGroupBudgets(now);
            return ValueTask.FromResult(TryConsumeLocked(_budget, now, limit, window));
        }
    }

    /// <inheritdoc />
    public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RemoveEndedGroupBudgets(now);
            Expire(_budget, now, window);
            return ValueTask.FromResult(_budget.Count);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TryConsumeGroupBudgetAsync(string group, DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        lock (_gate)
        {
            RemoveEndedGroupBudgets(now);
            if (!_groupBudgets.TryGetValue(group, out var budget))
            {
                budget = new Queue<DateTimeOffset>();
                _groupBudgets[group] = budget;
            }

            // Recorded before the unit is saved, so the file holds both (a file store that cannot save restores both).
            // When the unit is refused, the end is later than needed: the budget is only kept longer.
            _groupBudgetEnds[group] = EndOf(budget.Count > 0 && budget.Max() > now ? budget.Max() : now, window);
            try
            {
                return ValueTask.FromResult(TryConsumeLocked(budget, now, limit, window));
            }
            finally
            {
                // An empty budget is not kept: one entry per group that spent something within its window. (Looked up
                // again: a file store that could not save the change restored its saved state meanwhile.)
                if (_groupBudgets.TryGetValue(group, out var kept) && kept.Count == 0)
                {
                    _groupBudgets.Remove(group);
                    _groupBudgetEnds.Remove(group);
                }
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<int> GetGroupBudgetUsedAsync(string group, DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        lock (_gate)
        {
            RemoveEndedGroupBudgets(now);
            if (!_groupBudgets.TryGetValue(group, out var budget))
            {
                return ValueTask.FromResult(0);
            }

            Expire(budget, now, window);
            if (budget.Count == 0)
            {
                _groupBudgets.Remove(group);
                _groupBudgetEnds.Remove(group);
                return ValueTask.FromResult(0);
            }

            // The window of the latest call counts (a configuration reload may change it).
            _groupBudgetEnds[group] = EndOf(budget.Max(), window);
            return ValueTask.FromResult(budget.Count);
        }
    }

    private static DateTimeOffset EndOf(DateTimeOffset last, TimeSpan window)
        => window >= DateTimeOffset.MaxValue - last ? DateTimeOffset.MaxValue : last + window;

    // Caller holds the lock: removes the budgets of groups whose every unit has left its window. Not saved by itself (the
    // budgets left out are empty either way); the next change saves the file without them. A budget whose end is not
    // known (none recorded) is kept until a call about its group records one.
    private void RemoveEndedGroupBudgets(DateTimeOffset now)
    {
        if (_groupBudgetEnds.Count == 0)
        {
            return;
        }

        List<string>? ended = null;
        foreach (var (group, end) in _groupBudgetEnds)
        {
            if (now >= end)
            {
                (ended ??= []).Add(group);
            }
        }

        foreach (var group in ended ?? [])
        {
            _groupBudgets.Remove(group);
            _groupBudgetEnds.Remove(group);
        }
    }

    // Caller holds the lock: the global budget and the budget of each group follow the same rule.
    private bool TryConsumeLocked(Queue<DateTimeOffset> budget, DateTimeOffset now, int limit, TimeSpan window)
    {
        Expire(budget, now, window);
        if (budget.Count >= limit)
        {
            return false;
        }

        budget.Enqueue(now);
        OnChanged();
        return true;
    }

    /// <inheritdoc />
    public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_gate)
        {
            if (maxRuns <= 0)
            {
                return ValueTask.CompletedTask;
            }

            _runs.AddFirst(run);
            while (_runs.Count > maxRuns)
            {
                _runs.RemoveLast();
            }

            OnChanged();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<HealingRunRecord>>(_runs.Take(maxRuns).ToArray());
        }
    }

    /// <inheritdoc />
    public bool SupportsConditionHistory => true;

    /// <inheritdoc />
    public bool SupportsDocuments => true;

    /// <inheritdoc />
    public ValueTask AppendConditionEventAsync(ConditionEventRecord conditionEvent, int maxEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conditionEvent);
        lock (_gate)
        {
            if (maxEvents <= 0)
            {
                return ValueTask.CompletedTask;
            }

            _events.AddFirst(conditionEvent);
            while (_events.Count > maxEvents)
            {
                _events.RemoveLast();
            }

            OnChanged();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ConditionEventRecord>> GetConditionEventsAsync(int maxEvents, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<ConditionEventRecord>>(_events.Take(Math.Max(0, maxEvents)).ToArray());
        }
    }

    /// <inheritdoc />
    public ValueTask<string?> GetDocumentAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            return ValueTask.FromResult(_documents.TryGetValue(key, out var document) ? document : null);
        }
    }

    /// <inheritdoc />
    public ValueTask SetDocumentAsync(string key, string document, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            if (!_documents.TryGetValue(key, out var current) || !string.Equals(current, document, StringComparison.Ordinal))
            {
                _documents[key] = document;
                OnChanged();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(new ControlState
            {
                Pause = _pause,
                GroupPauses = _groupPauses.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => GroupPauseState.From(p.Key, p.Value)).ToArray(),
                Switches = _switches.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray(),
                Approvals = _approvals.Values.OrderBy(a => a.Request.RequestedAt).ToArray(),
            });
        }
    }

    private void SetSwitchLocked(string name, SwitchState? state)
    {
        var changed = state is null ? _switches.Remove(name) : !_switches.TryGetValue(name, out var current) || current != state;
        if (state is not null)
        {
            _switches[name] = state;
        }

        if (changed)
        {
            OnChanged();
        }
    }

    // Decision order. A change that makes Homeostat do less (a pause, a switch off, a release) may be written again with
    // the same decision time (a deferred change retried), so a tie is accepted; a change that lets it act (a resume, a
    // switch on, a claim) wins only when strictly later, so a late one never ties with a newer stop.
    private static bool InOrder(DateTimeOffset decidedAt, DateTimeOffset latest, bool lessAction)
        => lessAction ? decidedAt >= latest : decidedAt > latest;

    /// <inheritdoc />
    public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(SetPauseInOrder(group: null, pause, decidedAt));

    /// <inheritdoc />
    public ValueTask<bool> TrySetGroupPauseAsync(string group, PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return ValueTask.FromResult(SetPauseInOrder(group, pause, decidedAt));
    }

    // The global pause (group null) and the pause of each group: the same decision order, each on its own.
    private bool SetPauseInOrder(string? group, PauseState? pause, DateTimeOffset decidedAt)
    {
        lock (_gate)
        {
            var hadDecision = group is null || _groupPauseDecidedAt.ContainsKey(group);
            var previous = group is null ? _pauseDecidedAt : _groupPauseDecidedAt.GetValueOrDefault(group, DateTimeOffset.MinValue);
            if (!InOrder(decidedAt, previous, lessAction: pause is not null))
            {
                return false;
            }

            if (group is not null && pause is not null && !_groupPauses.ContainsKey(group) && _groupPauses.Count >= ControlState.MaxGroupPauses)
            {
                throw new InvalidOperationException(
                    $"At most {ControlState.MaxGroupPauses} healing groups can be paused at once: resume groups that no longer need their pause.");
            }

            SetPauseDecision(group, decidedAt);
            try
            {
                if (group is null)
                {
                    _pause = pause;
                }
                else if (pause is null)
                {
                    _groupPauses.Remove(group);
                }
                else
                {
                    _groupPauses[group] = pause;
                }

                OnChanged();
            }
            catch
            {
                // Not saved (the file store undoes the change): neither is the decision.
                if (hadDecision)
                {
                    SetPauseDecision(group, previous);
                }
                else
                {
                    _groupPauseDecidedAt.Remove(group!);
                }

                throw;
            }

            return true;
        }
    }

    // Caller holds the lock.
    private void SetPauseDecision(string? group, DateTimeOffset decidedAt)
    {
        if (group is null)
        {
            _pauseDecidedAt = decidedAt;
        }
        else
        {
            _groupPauseDecidedAt[group] = decidedAt;
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            if (!InOrder(decidedAt, Decided(name), lessAction: state is null))
            {
                return ValueTask.FromResult(false);
            }

            Decide(name, decidedAt, () => SetSwitchLocked(name, state));
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.Condition);
        lock (_gate)
        {
            // A renewal (or an abandonment) carries the claim's At and another RenewedAt: it only updates a claim that is
            // still there, by the same instance. A new claim has RenewedAt equal to At, or none.
            var renewal = state.RenewedAt is { } renewed && renewed != state.At;
            if (_switches.TryGetValue(state.Name, out var current))
            {
                if (!string.Equals(current.Condition, state.Condition, StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(false);   // another owner
                }

                if (renewal
                    ? !string.Equals(current.Instance, state.Instance, StringComparison.Ordinal) || current.At != state.At
                    : state.At < Decided(state.Name))
                {
                    return ValueTask.FromResult(false);   // claimed again since (by another instance, or a newer run)
                }
            }
            else if (renewal || !InOrder(state.At, Decided(state.Name), lessAction: false))
            {
                // Off: a renewal of a switch released meanwhile, or a claim decided at or before the latest decision.
                return ValueTask.FromResult(false);
            }

            // A renewal is not a decision: it keeps the claim alive, and must never make a person's later "off" look old.
            var decision = renewal || state.At <= Decided(state.Name) ? Decided(state.Name) : state.At;
            Decide(state.Name, decision, () =>
            {
                _switches[state.Name] = state;
                OnChanged();
            });
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        lock (_gate)
        {
            // Refused when the condition claimed the switch again, or its owner renewed the claim, after the release was
            // decided (an instance releasing an orphan as of the renewal it saw).
            if (!_switches.TryGetValue(name, out var current)
                || !string.Equals(current.Condition, condition, StringComparison.Ordinal)
                || (instance is not null && !string.Equals(current.Instance, instance, StringComparison.Ordinal))
                || !InOrder(decidedAt, Decided(name), lessAction: true)
                || current.RenewedAt > decidedAt)
            {
                return ValueTask.FromResult(false);
            }

            Decide(name, decidedAt > Decided(name) ? decidedAt : Decided(name), () =>
            {
                _switches.Remove(name);
                OnChanged();
            });
            return ValueTask.FromResult(true);
        }
    }

    // Caller holds the lock.
    private DateTimeOffset Decided(string name) => _switchDecidedAt.TryGetValue(name, out var latest) ? latest : DateTimeOffset.MinValue;

    // Caller holds the lock: records the decision with the change, or neither if the change is not saved.
    private void Decide(string name, DateTimeOffset decidedAt, Action change)
    {
        var had = _switchDecidedAt.TryGetValue(name, out var previous);
        _switchDecidedAt[name] = decidedAt;
        try
        {
            change();
        }
        catch
        {
            if (had)
            {
                _switchDecidedAt[name] = previous;
            }
            else
            {
                _switchDecidedAt.Remove(name);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);
        lock (_gate)
        {
            // A request whose instance stopped before collecting the answer is dropped a day after it expired, as the
            // Redis store does, so the requests do not grow for ever. Measured by the new request's clock (the store
            // has none): the clock of the instances that write them.
            var requestedAt = approval.Request.RequestedAt;
            var cutoff = requestedAt > DateTimeOffset.MinValue + AbandonedApprovalRetention ? requestedAt - AbandonedApprovalRetention : DateTimeOffset.MinValue;
            foreach (var id in _approvals.Where(a => a.Value.Request.ExpiresAt < cutoff).Select(a => a.Key).ToArray())
            {
                _approvals.Remove(id);
            }

            _approvals[approval.Request.Id] = approval;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>How long after it expired a request nobody collected is kept.</summary>
    internal static readonly TimeSpan AbandonedApprovalRetention = TimeSpan.FromDays(1);

    /// <inheritdoc />
    public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            if (!_approvals.TryGetValue(id, out var approval) || approval.Status is not null)
            {
                return ValueTask.FromResult(false);
            }

            _approvals[id] = new ApprovalRecord { Request = approval.Request, Instance = approval.Instance, Status = status, ResolvedBy = resolvedBy };
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            _approvals.Remove(id);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Called under the lock after every change; lets a derived store persist the state.</summary>
    protected virtual void OnChanged()
    {
    }

    /// <summary>Copies the state, under the lock, for a derived store to persist.</summary>
    internal StoreSnapshot Snapshot()
        => new(new Dictionary<string, PlanState>(_plans, StringComparer.Ordinal), _budget.ToArray(), _runs.ToArray())
        {
            Events = _events.ToArray(),
            Documents = new Dictionary<string, string>(_documents, StringComparer.Ordinal),
            Pause = _pause,
            GroupPauses = _groupPauses.Select(p => GroupPauseState.From(p.Key, p.Value)).ToArray(),
            Switches = _switches.Values.ToArray(),
            GroupBudgets = _groupBudgets.ToDictionary(b => b.Key, b => (IReadOnlyList<DateTimeOffset>)b.Value.ToArray(), StringComparer.Ordinal),
            GroupBudgetEnds = new Dictionary<string, DateTimeOffset>(_groupBudgetEnds, StringComparer.Ordinal),
        };

    /// <summary>Replaces the state, for a derived store loading persisted data.</summary>
    internal void Restore(StoreSnapshot snapshot)
    {
        lock (_gate)
        {
            _plans.Clear();
            foreach (var (key, state) in snapshot.Plans)
            {
                _plans[key] = state;
            }

            _budget.Clear();
            foreach (var spent in snapshot.Budget.Order())
            {
                _budget.Enqueue(spent);
            }

            _runs.Clear();
            foreach (var run in snapshot.Runs)
            {
                _runs.AddLast(run);
            }

            _events.Clear();
            foreach (var conditionEvent in snapshot.Events)
            {
                _events.AddLast(conditionEvent);
            }

            _documents.Clear();
            foreach (var (key, document) in snapshot.Documents)
            {
                _documents[key] = document;
            }

            _pause = snapshot.Pause;
            _groupPauses.Clear();
            foreach (var pause in snapshot.GroupPauses)
            {
                _groupPauses[pause.Group] = pause.ToPause();
            }

            _groupBudgets.Clear();
            _groupBudgetEnds.Clear();
            foreach (var (group, spent) in snapshot.GroupBudgets)
            {
                if (spent.Count > 0)
                {
                    _groupBudgets[group] = new Queue<DateTimeOffset>(spent.Order());
                    if (snapshot.GroupBudgetEnds.TryGetValue(group, out var end))
                    {
                        _groupBudgetEnds[group] = end;
                    }
                }
            }

            _switches.Clear();
            foreach (var state in snapshot.Switches)
            {
                _switches[state.Name] = state;
            }
        }
    }

    // Caller holds the lock.
    private static void Expire(Queue<DateTimeOffset> budget, DateTimeOffset now, TimeSpan window)
    {
        while (budget.Count > 0 && now - budget.Peek() >= window)
        {
            budget.Dequeue();
        }
    }

    internal sealed record StoreSnapshot(
        IReadOnlyDictionary<string, PlanState> Plans,
        IReadOnlyList<DateTimeOffset> Budget,
        IReadOnlyList<HealingRunRecord> Runs)
    {
        public IReadOnlyList<ConditionEventRecord> Events { get; init; } = [];

        public IReadOnlyDictionary<string, string> Documents { get; init; } = new Dictionary<string, string>();

        public PauseState? Pause { get; init; }

        public IReadOnlyList<SwitchState> Switches { get; init; } = [];

        public IReadOnlyList<GroupPauseState> GroupPauses { get; init; } = [];

        public IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> GroupBudgets { get; init; } = new Dictionary<string, IReadOnlyList<DateTimeOffset>>();

        public IReadOnlyDictionary<string, DateTimeOffset> GroupBudgetEnds { get; init; } = new Dictionary<string, DateTimeOffset>();
    }
}
