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

    // When the latest decision about the pause and about each switch was taken: an older write is refused. Kept in
    // memory only (a late write does not outlive a restart).
    private DateTimeOffset _pauseDecidedAt = DateTimeOffset.MinValue;
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
            Expire(now, window);
            if (_budget.Count >= limit)
            {
                return ValueTask.FromResult(false);
            }

            _budget.Enqueue(now);
            OnChanged();
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Expire(now, window);
            return ValueTask.FromResult(_budget.Count);
        }
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
    {
        lock (_gate)
        {
            if (!InOrder(decidedAt, _pauseDecidedAt, lessAction: pause is not null))
            {
                return ValueTask.FromResult(false);
            }

            var previous = _pauseDecidedAt;
            _pauseDecidedAt = decidedAt;
            try
            {
                _pause = pause;
                OnChanged();
            }
            catch
            {
                // Not saved (the file store undoes the change): neither is the decision.
                _pauseDecidedAt = previous;
                throw;
            }

            return ValueTask.FromResult(true);
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

            var decision = renewal ? state.RenewedAt!.Value : state.At;
            Decide(state.Name, decision > Decided(state.Name) ? decision : Decided(state.Name), () =>
            {
                _switches[state.Name] = state;
                OnChanged();
            });
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
        => Release(name, condition, instance, decidedAt);

    private ValueTask<bool> Release(string name, string condition, string? instance, DateTimeOffset? decidedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        lock (_gate)
        {
            if (!_switches.TryGetValue(name, out var current)
                || !string.Equals(current.Condition, condition, StringComparison.Ordinal)
                || (instance is not null && !string.Equals(current.Instance, instance, StringComparison.Ordinal))
                || (decidedAt is { } at && !InOrder(at, Decided(name), lessAction: true)))
            {
                return ValueTask.FromResult(false);
            }

            Decide(name, decidedAt is { } when && when > Decided(name) ? when : Decided(name), () =>
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
            _approvals[approval.Request.Id] = approval;
        }

        return ValueTask.CompletedTask;
    }

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
            Switches = _switches.Values.ToArray(),
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
            _switches.Clear();
            foreach (var state in snapshot.Switches)
            {
                _switches[state.Name] = state;
            }
        }
    }

    // Caller holds the lock.
    private void Expire(DateTimeOffset now, TimeSpan window)
    {
        while (_budget.Count > 0 && now - _budget.Peek() >= window)
        {
            _budget.Dequeue();
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
    }
}
