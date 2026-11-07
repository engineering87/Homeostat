using Homeostat.Healing;

namespace Homeostat.Persistence;

/// <summary>
/// Where Homeostat keeps the state that must outlive a run or be the same on every instance: plan state (cooldowns,
/// backoff, holds), the action budget, the run and condition history, and the control state (pause, application
/// switches, approval requests). The default store is in memory; a shared store (for example <c>Homeostat.Redis</c>)
/// makes that state survive restarts and span every instance.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe. When the store fails, Homeostat <b>fails closed</b>: it does not start
/// a run whose cooldown or budget it cannot check. Members added after 1.0 will have default implementations, with a
/// <c>Supports*</c> property when the default does nothing (as <see cref="SupportsConditionHistory"/> and
/// <see cref="SupportsDocuments"/>), so stores written for 1.0 keep compiling and Homeostat knows what they keep.
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
    /// Whether the store keeps documents (<see cref="GetDocumentAsync"/>, <see cref="SetDocumentAsync"/>). Without them,
    /// seasonal baselines are learned again after every restart, and Homeostat logs it once at startup.
    /// </summary>
    bool SupportsDocuments => false;

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

    /// <summary>The pause, the application switches and the approval requests.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default);

    // The pause and the switches are written in decision order: every write carries the time the decision was taken,
    // and the store applies it only if no later decision about the same pause or switch is recorded. A write that
    // timed out on the caller's side but that the store runs afterwards then cannot undo a newer decision. On a tie, a
    // decision that makes Homeostat do less (a pause, a switch off, a release) is accepted, one that lets it act (a
    // resume, a switch on, a claim) is not. Each operation is atomic.

    /// <summary>Sets the pause, or removes it with <c>null</c> (a resume), <b>in decision order</b>.</summary>
    /// <param name="pause">The pause, or <c>null</c> to resume.</param>
    /// <param name="decidedAt">When the decision was taken (<see cref="PauseState.At"/> for a pause).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if applied; <c>false</c> if a later decision about the pause is already in the store.</returns>
    ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns an application switch on (replacing its previous state) or, with <c>null</c>, off, <b>in decision order</b>:
    /// decisions about a switch include turning it on or off and a condition claiming or releasing it.
    /// </summary>
    /// <param name="name">Name of the switch.</param>
    /// <param name="state">The switch, or <c>null</c> to turn it off.</param>
    /// <param name="decidedAt">When the decision was taken.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if applied; <c>false</c> if a later decision about the switch is already in the store.</returns>
    ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns an application switch on for a condition's cluster plan <b>atomically</b>, in decision order
    /// (<see cref="SwitchState.At"/> is the decision): only if it is owned by the same condition and not claimed later
    /// since, or off and not decided about at or after the claim. A switch a person turned on, or that another condition
    /// owns, is left as it is. A <b>renewal</b> (<see cref="SwitchState.RenewedAt"/> differs from
    /// <see cref="SwitchState.At"/>) only updates the claim it renews: still there, made by the same instance at the same
    /// <see cref="SwitchState.At"/>. A renewal is not a decision: it keeps the claim's decision time, so a person's change
    /// decided after the claim still applies, and it records <see cref="SwitchState.RenewedAt"/>, so that a release decided
    /// before the renewal is refused (see <see cref="TryReleaseSwitchAsync"/>).
    /// </summary>
    /// <param name="state">The switch, with <see cref="SwitchState.Condition"/> set.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the switch is now owned by the condition.</returns>
    ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns an application switch off <b>atomically</b>, in decision order, only if it is still owned by the condition
    /// (and, when <paramref name="instance"/> is given, was turned on by that instance): a switch a person took over, or
    /// that the condition claimed or renewed after <paramref name="decidedAt"/>, stays on.
    /// </summary>
    /// <param name="name">Name of the switch.</param>
    /// <param name="condition">The owning condition.</param>
    /// <param name="instance">The instance that turned it on, or <c>null</c> for any.</param>
    /// <param name="decidedAt">When the release was decided.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the switch was turned off.</returns>
    ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default);

    /// <summary>Adds a pending approval request.</summary>
    /// <param name="approval">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers a request <b>atomically</b>, only if it is still pending: of two answers given at the same time on two
    /// instances, exactly one wins.
    /// </summary>
    /// <param name="id">Request identifier.</param>
    /// <param name="status">The answer.</param>
    /// <param name="resolvedBy">Who answered, when known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if this call answered the request.</returns>
    ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default);

    /// <summary>Removes a request, once the waiting instance has collected the answer.</summary>
    /// <param name="id">Request identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default);
}
