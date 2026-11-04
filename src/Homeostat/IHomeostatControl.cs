using Homeostat.Conditions;
using Homeostat.Diagnostics;
using Homeostat.Guardrails;
using Homeostat.Persistence;

namespace Homeostat;

/// <summary>Runtime control and inspection of Homeostat.</summary>
public interface IHomeostatControl
{
    /// <summary>Whether new healing steps are suspended, by <see cref="PauseAsync"/> or a maintenance window.</summary>
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
