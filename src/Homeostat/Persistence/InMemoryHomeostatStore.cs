namespace Homeostat.Persistence;

/// <summary>Default store: everything in process memory, lost on restart, not shared between instances.</summary>
public class InMemoryHomeostatStore : IHomeostatStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PlanState> _plans = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _budget = new();
    private readonly LinkedList<HealingRunRecord> _runs = new();

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

    /// <summary>Called under the lock after every change; lets a derived store persist the state.</summary>
    protected virtual void OnChanged()
    {
    }

    /// <summary>Copies the state, under the lock, for a derived store to persist.</summary>
    private protected StoreSnapshot Snapshot()
        => new(new Dictionary<string, PlanState>(_plans, StringComparer.Ordinal), _budget.ToArray(), _runs.ToArray());

    /// <summary>Replaces the state, for a derived store loading persisted data.</summary>
    private protected void Restore(StoreSnapshot snapshot)
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

    private protected sealed record StoreSnapshot(
        IReadOnlyDictionary<string, PlanState> Plans,
        IReadOnlyList<DateTimeOffset> Budget,
        IReadOnlyList<HealingRunRecord> Runs);
}
