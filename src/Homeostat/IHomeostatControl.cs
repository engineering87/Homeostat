using Homeostat.Conditions;
using Homeostat.Diagnostics;
using Homeostat.Guardrails;
using Homeostat.Persistence;

namespace Homeostat;

/// <summary>Runtime control and inspection of Homeostat.</summary>
public interface IHomeostatControl
{
    /// <summary>Whether new healing steps are suspended (by <see cref="Pause"/>, <see cref="PauseUntil"/> or a maintenance window).</summary>
    bool IsPaused { get; }

    /// <summary>Why Homeostat is paused, or <c>null</c> when it is not.</summary>
    string? PauseReason { get; }

    /// <summary>Whether actions are only logged (<see cref="HomeostatOptions.DryRun"/>).</summary>
    bool IsDryRun { get; }

    /// <summary>
    /// Kill switch: no new action starts until <see cref="Resume"/> is called.
    /// Conditions keep being tracked, so the status remains observable.
    /// </summary>
    void Pause();

    /// <summary>Pauses until the given time, then resumes automatically (unless <see cref="Pause"/> was also called).</summary>
    /// <param name="until">When to resume.</param>
    void PauseUntil(DateTimeOffset until);

    /// <summary>Lets healing plans run again after <see cref="Pause"/> or <see cref="PauseUntil"/>. Maintenance windows still apply.</summary>
    void Resume();

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
