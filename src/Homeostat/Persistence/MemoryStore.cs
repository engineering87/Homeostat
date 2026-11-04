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

    /// <inheritdoc />
    public ValueTask SetPauseAsync(PauseState? pause, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _pause = pause;
            OnChanged();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SetSwitchAsync(string name, SwitchState? state, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
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

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(state.Condition);
        lock (_gate)
        {
            if (_switches.TryGetValue(state.Name, out var current) && !string.Equals(current.Condition, state.Condition, StringComparison.Ordinal))
            {
                return ValueTask.FromResult(false);
            }

            _switches[state.Name] = state;
            OnChanged();
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(condition);
        lock (_gate)
        {
            if (!_switches.TryGetValue(name, out var current)
                || !string.Equals(current.Condition, condition, StringComparison.Ordinal)
                || (instance is not null && !string.Equals(current.Instance, instance, StringComparison.Ordinal)))
            {
                return ValueTask.FromResult(false);
            }

            _switches.Remove(name);
            OnChanged();
            return ValueTask.FromResult(true);
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
