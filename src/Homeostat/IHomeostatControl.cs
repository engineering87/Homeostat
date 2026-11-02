using Homeostat.Conditions;
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

    /// <summary>Current use of the action budget, from the store.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<ActionBudgetStatus> GetBudgetAsync(CancellationToken cancellationToken = default);
}
