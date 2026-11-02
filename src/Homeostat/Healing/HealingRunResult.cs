namespace Homeostat.Healing;

/// <summary>How a healing run ended.</summary>
public enum HealingRunOutcome
{
    /// <summary>A step was verified as healed.</summary>
    Healed = 0,

    /// <summary>Every step was tried without success.</summary>
    NotHealed = 1,

    /// <summary>Verification found no evidence either way; the run stopped without escalating.</summary>
    Inconclusive = 2,

    /// <summary>Dry-run: steps were logged, not executed.</summary>
    DryRun = 3,

    /// <summary>The global action budget was exhausted.</summary>
    BudgetExhausted = 4,

    /// <summary>Homeostat was paused during the run.</summary>
    Paused = 5,

    /// <summary>A step requiring approval was rejected or not approved in time.</summary>
    NotApproved = 6,

    /// <summary>A condition listed in <c>SuppressedBy</c> became active during the run.</summary>
    Suppressed = 7,

    /// <summary>The condition cleared before the next step started, so the step was not needed.</summary>
    ConditionCleared = 8,

    /// <summary>The application stopped during the run.</summary>
    Cancelled = 9,

    /// <summary>An unexpected error interrupted the run.</summary>
    Failed = 10,
}

/// <summary>Result of one executed step.</summary>
/// <param name="ConditionName">Condition being healed.</param>
/// <param name="Step">Zero-based index of the step.</param>
/// <param name="Name">Step name.</param>
/// <param name="Succeeded">Whether the action completed without throwing or timing out.</param>
/// <param name="Verification">Verification outcome; <c>null</c> when the action did not succeed.</param>
/// <param name="Error">The exception thrown by the action, or a <see cref="TimeoutException"/>.</param>
/// <param name="UndoSucceeded">
/// Whether the compensating action (<see cref="HealingPlanBuilder.UndoWith{TAction}"/>) completed; <c>null</c> when
/// it did not run.
/// </param>
public sealed record HealingStepResult(
    string ConditionName,
    int Step,
    string Name,
    bool Succeeded,
    VerificationOutcome? Verification,
    Exception? Error,
    bool? UndoSucceeded = null);

/// <summary>Result of a complete healing run.</summary>
/// <param name="ConditionName">Condition being healed.</param>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Steps">Executed steps, in order.</param>
/// <param name="StartedAt">When the run started.</param>
/// <param name="CompletedAt">When the run ended.</param>
public sealed record HealingRunResult(
    string ConditionName,
    HealingRunOutcome Outcome,
    IReadOnlyList<HealingStepResult> Steps,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);
