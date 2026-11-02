using Homeostat.Healing;

namespace Homeostat.Testing;

/// <summary>Observer that records completed runs and lets tests wait for them.</summary>
internal sealed class RunRecorder : IHealingObserver
{
    private readonly object _gate = new();
    private readonly List<HealingRunResult> _runs = [];
    private readonly Dictionary<string, int> _consumed = new(StringComparer.Ordinal);
    private readonly List<(string Condition, TaskCompletionSource<HealingRunResult> Waiter)> _waiters = [];

    public IReadOnlyList<HealingRunResult> Runs
    {
        get
        {
            lock (_gate)
            {
                return _runs.ToArray();
            }
        }
    }

    public void OnRunCompleted(HealingRunResult run)
    {
        TaskCompletionSource<HealingRunResult>? waiter = null;

        lock (_gate)
        {
            _runs.Add(run);
            var index = _waiters.FindIndex(w => w.Condition == run.ConditionName);
            if (index >= 0)
            {
                waiter = _waiters[index].Waiter;
                _waiters.RemoveAt(index);
                Consume(run.ConditionName);
            }
        }

        waiter?.TrySetResult(run);
    }

    /// <summary>
    /// Returns the oldest run of the condition not returned yet, or waits for the next one.
    /// A canceled wait is withdrawn, so it does not consume a later run.
    /// </summary>
    public async Task<HealingRunResult> NextAsync(string condition, CancellationToken cancellationToken)
    {
        TaskCompletionSource<HealingRunResult> waiter;

        lock (_gate)
        {
            var consumed = _consumed.GetValueOrDefault(condition);
            var available = _runs.Where(r => r.ConditionName == condition).Skip(consumed).FirstOrDefault();
            if (available is not null)
            {
                Consume(condition);
                return available;
            }

            waiter = new TaskCompletionSource<HealingRunResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((condition, waiter));
        }

        using var registration = cancellationToken.Register(() =>
        {
            int removed;
            lock (_gate)
            {
                removed = _waiters.RemoveAll(w => ReferenceEquals(w.Waiter, waiter));
            }

            // If nothing was removed, OnRunCompleted has already taken this waiter and consumed a run for it:
            // let it deliver that run instead of losing it.
            if (removed > 0)
            {
                waiter.TrySetCanceled(cancellationToken);
            }
        });

        return await waiter.Task.ConfigureAwait(false);
    }

    // Caller holds the lock.
    private void Consume(string condition) => _consumed[condition] = _consumed.GetValueOrDefault(condition) + 1;
}
