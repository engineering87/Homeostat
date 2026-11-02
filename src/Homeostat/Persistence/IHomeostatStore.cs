namespace Homeostat.Persistence;

/// <summary>
/// Where Homeostat keeps the state that must outlive a run: plan state (cooldowns, backoff, holds), the action
/// budget and the run history. The default store is in memory; a shared store (for example
/// <c>Homeostat.Redis</c>) makes cooldowns, budget and history survive restarts and span every instance.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe. When the store fails, Homeostat <b>fails closed</b>: it does not start
/// a run whose cooldown or budget it cannot check.
/// </remarks>
public interface IHomeostatStore
{
    /// <summary>Returns the state of a plan, or <see cref="PlanState.Empty"/>.</summary>
    /// <param name="key">Plan key: the condition name, prefixed with the instance for instance-scoped plans.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies an update to the state of a plan <b>atomically</b> (as <see cref="PlanStateUpdate.ApplyTo"/> describes)
    /// and returns the new state. With a shared store, concurrent updates from several instances must not lose each
    /// other's changes.
    /// </summary>
    /// <param name="key">Plan key.</param>
    /// <param name="update">The change.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default);

    /// <summary>Consumes one unit of the action budget if fewer than <paramref name="limit"/> were used within <paramref name="window"/>.</summary>
    /// <param name="now">Current time.</param>
    /// <param name="limit">Maximum actions per window.</param>
    /// <param name="window">Sliding window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the action may run.</returns>
    ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default);

    /// <summary>Returns how many actions were consumed within <paramref name="window"/>.</summary>
    /// <param name="now">Current time.</param>
    /// <param name="window">Sliding window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default);

    /// <summary>Adds a run to the history, keeping at most <paramref name="maxRuns"/> runs.</summary>
    /// <param name="run">The run.</param>
    /// <param name="maxRuns">History size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default);

    /// <summary>Returns the most recent runs, newest first.</summary>
    /// <param name="maxRuns">Maximum number of runs.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the store keeps the condition history (<see cref="AppendConditionEventAsync"/>). A store written before
    /// the condition history existed does not, and Homeostat then keeps the history in process memory only.
    /// </summary>
    bool SupportsConditionHistory => false;

    /// <summary>Adds a condition event to the history, keeping at most <paramref name="maxEvents"/> events.</summary>
    /// <param name="conditionEvent">The event.</param>
    /// <param name="maxEvents">History size.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask AppendConditionEventAsync(ConditionEventRecord conditionEvent, int maxEvents, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <summary>Returns the most recent condition events, newest first.</summary>
    /// <param name="maxEvents">Maximum number of events.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<IReadOnlyList<ConditionEventRecord>> GetConditionEventsAsync(int maxEvents, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<ConditionEventRecord>>([]);

    /// <summary>
    /// Returns a small document that a feature keeps between restarts (for example the learned baseline of a seasonal
    /// rule), or <c>null</c> when there is none. A store that does not support documents returns <c>null</c>.
    /// </summary>
    /// <param name="key">Document key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<string?> GetDocumentAsync(string key, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<string?>(null);

    /// <summary>Saves a small document (up to about 150 KB of JSON), replacing the previous one with the same key.</summary>
    /// <param name="key">Document key.</param>
    /// <param name="document">Document content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SetDocumentAsync(string key, string document, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
