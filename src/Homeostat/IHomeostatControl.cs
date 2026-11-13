using Homeostat.Conditions;
using Homeostat.Diagnostics;
using Homeostat.Guardrails;
using Homeostat.Persistence;

namespace Homeostat;

/// <summary>Runtime control and inspection of Homeostat.</summary>
public interface IHomeostatControl
{
    /// <summary>
    /// Whether new healing steps are suspended for every plan, by <see cref="PauseAsync"/> or a maintenance window without
    /// groups. The pause of a healing group is reported by <see cref="IsGroupPaused"/>.
    /// </summary>
    /// <remarks>Reads this instance's view of the store and never waits.</remarks>
    bool IsPaused { get; }

    /// <summary>Why Homeostat is paused (who paused it, until when, or which maintenance window), or <c>null</c>.</summary>
    string? PauseReason { get; }

    /// <summary>Whether actions are only logged (<see cref="HomeostatOptions.DryRun"/>).</summary>
    bool IsDryRun { get; }

    /// <summary>
    /// Kill switch: no new action starts until <see cref="ResumeAsync"/> is called, or until <paramref name="until"/>.
    /// The pause is kept in the store: with a shared store it stops every instance within
    /// <see cref="HomeostatOptions.ControlRefreshInterval"/> and survives restarts. Conditions keep being tracked, so
    /// the status remains observable. A new call replaces the previous pause.
    /// </summary>
    /// <param name="until">When to resume by itself; <c>null</c> pauses until <see cref="ResumeAsync"/>.</param>
    /// <param name="by">Who paused, shown in <see cref="PauseReason"/> and the status.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask PauseAsync(DateTimeOffset? until = null, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>Lets healing plans run again after <see cref="PauseAsync"/>, on every instance. Maintenance windows still apply.</summary>
    /// <param name="by">Who resumed, recorded in the log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask ResumeAsync(string? by = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the plans of the conditions in a healing group (<see cref="ConditionBuilder.InGroup"/>) are suspended:
    /// by the global pause (<see cref="IsPaused"/>), by <see cref="PauseGroupAsync"/>, or by a maintenance window of the
    /// group (<see cref="MaintenanceWindow.ForGroups"/>). A group nobody configured can be paused too.
    /// </summary>
    /// <remarks>Reads this instance's view of the store and never waits. <c>true</c> until the control state is first read.</remarks>
    /// <param name="group">Name of the group.</param>
    bool IsGroupPaused(string group);

    /// <summary>
    /// Why the plans of a healing group are suspended, or <c>null</c>: the global <see cref="PauseReason"/> when Homeostat
    /// is paused (resuming the group would not be enough), otherwise who paused the group and until when, or which
    /// maintenance window of the group is open.
    /// </summary>
    /// <param name="group">Name of the group.</param>
    string? GetGroupPauseReason(string group);

    /// <summary>
    /// Pauses the plans of the conditions in a healing group only, exactly as <see cref="PauseAsync"/> pauses every plan:
    /// no run of the group starts, a run in progress stops before its next step (compensations still run), conditions
    /// keep being tracked, and the other groups go on. Kept in the store, so with a shared store it applies to every
    /// instance and survives restarts; when the store cannot take it, it applies on this instance at once and is written
    /// when the store answers. A new call replaces the group's previous pause. At most
    /// <see cref="ControlState.MaxGroupPauses"/> groups can be paused at once.
    /// </summary>
    /// <param name="group">Name of the group: not empty, at most 200 characters, no control characters.</param>
    /// <param name="until">When to resume by itself; <c>null</c> pauses until <see cref="ResumeGroupAsync"/>.</param>
    /// <param name="by">Who paused, shown in <see cref="GetGroupPauseReason"/> and the status.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The group name is not valid.</exception>
    /// <exception cref="InvalidOperationException"><see cref="ControlState.MaxGroupPauses"/> other groups are paused already.</exception>
    ValueTask PauseGroupAsync(string group, DateTimeOffset? until = null, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets the plans of a healing group run again after <see cref="PauseGroupAsync"/>, on every instance, as
    /// <see cref="ResumeAsync"/> does for the global pause: it must reach the store, or it does not happen (a resume that
    /// times out leaves the group paused). The global pause and the group's maintenance windows still apply.
    /// </summary>
    /// <param name="group">Name of the group.</param>
    /// <param name="by">Who resumed, recorded in the log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The group name is not valid.</exception>
    ValueTask ResumeGroupAsync(string group, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every healing group known on this instance, ordered by name: the groups of the registered conditions, the groups
    /// configured with <see cref="HomeostatBuilder.ConfigureGroup"/>, and the groups with a pause in the store.
    /// </summary>
    /// <remarks>Reads this instance's view of the store and never waits.</remarks>
    IReadOnlyList<HealingGroupStatus> GetGroups();

    /// <summary>
    /// Current use of the action budget of a healing group, from the store; <c>null</c> when the group has no budget of
    /// its own (<see cref="HealingGroupOptions.MaxActionsPerWindow"/> not set, or the group is not configured).
    /// </summary>
    /// <param name="group">Name of the group.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentException">The group name is not valid.</exception>
    ValueTask<ActionBudgetStatus?> GetGroupBudgetAsync(string group, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lifts the hold of a condition's plan after a rejected approval, and restarts its cooldown backoff, without waiting
    /// for the condition to clear: for example after the person who rejected the step fixed the cause by hand.
    /// </summary>
    /// <param name="condition">Name of the condition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>false</c> when no condition with a plan has this name.</returns>
    ValueTask<bool> ReleaseHoldAsync(string condition, CancellationToken cancellationToken = default);

    /// <summary>Current state of every registered condition.</summary>
    IReadOnlyList<ConditionSnapshot> GetConditions();

    /// <summary>Most recent completed runs, newest first, from the store (with a shared store, of every instance).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Latest condition events (activations and clears), newest first: from the store when it keeps the condition
    /// history (the built-in stores do; with a shared store, of every instance), otherwise from this process.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<IReadOnlyList<ConditionEventRecord>> GetConditionHistoryAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<ConditionEventRecord>>([]);

    /// <summary>
    /// What was exercised since <paramref name="since"/>: activations, runs and their outcomes, steps that never ran.
    /// Activations come from the condition history (see <see cref="GetConditionHistoryAsync"/>) and runs from the run
    /// history: when either does not reach back to <paramref name="since"/>, the report says so in
    /// <see cref="CoverageReport.Truncated"/>.
    /// </summary>
    /// <param name="since">Start of the period, for example the start of a game day.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<CoverageReport> GetCoverageAsync(DateTimeOffset since, CancellationToken cancellationToken = default);

    /// <summary>Current use of the action budget, from the store.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<ActionBudgetStatus> GetBudgetAsync(CancellationToken cancellationToken = default);
}
