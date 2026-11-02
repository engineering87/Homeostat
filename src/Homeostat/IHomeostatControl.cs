using Homeostat.Conditions;
using Homeostat.Guardrails;
using Homeostat.Healing;

namespace Homeostat;

/// <summary>Runtime control and inspection of Homeostat.</summary>
public interface IHomeostatControl
{
    /// <summary>Whether new healing steps are suspended.</summary>
    bool IsPaused { get; }

    /// <summary>Whether actions are only logged (<see cref="HomeostatOptions.DryRun"/>).</summary>
    bool IsDryRun { get; }

    /// <summary>
    /// Kill switch: no new action starts until <see cref="Resume"/> is called.
    /// Conditions keep being tracked, so the status remains observable.
    /// </summary>
    void Pause();

    /// <summary>Lets healing plans run again after <see cref="Pause"/>.</summary>
    void Resume();

    /// <summary>Current state of every registered condition.</summary>
    IReadOnlyList<ConditionSnapshot> GetConditions();

    /// <summary>Most recent completed runs, newest first (up to <see cref="HomeostatOptions.RunHistorySize"/>).</summary>
    IReadOnlyList<HealingRunResult> GetRecentRuns();

    /// <summary>Current use of the global action budget.</summary>
    ActionBudgetStatus GetBudget();
}
