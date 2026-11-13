using System.Text.Json.Serialization;

namespace Homeostat.Healing;

/// <summary>How a healing run ended.</summary>
/// <remarks>
/// Numbered from 1 on purpose: 0 (<c>default</c>) is deliberately not a member, so a value never set is caught as
/// undefined and never reads as a success.
/// </remarks>
public enum HealingRunOutcome
{
    /// <summary>A step was verified as healed.</summary>
    Healed = 1,

    /// <summary>Every step was tried without success.</summary>
    NotHealed = 2,

    /// <summary>Verification found no evidence either way; the run stopped without escalating.</summary>
    Inconclusive = 3,

    /// <summary>Dry-run: steps were logged, not executed.</summary>
    DryRun = 4,

    /// <summary>The global action budget, or the budget of the condition's healing group, was exhausted.</summary>
    BudgetExhausted = 5,

    /// <summary>Homeostat, or the condition's healing group, was paused (by a person or a maintenance window).</summary>
    Paused = 6,

    /// <summary>A step requiring approval was rejected or not approved in time.</summary>
    NotApproved = 7,

    /// <summary>A condition listed in <c>SuppressedBy</c> became active during the run.</summary>
    Suppressed = 8,

    /// <summary>The condition cleared before the next step started, so the step was not needed.</summary>
    ConditionCleared = 9,

    /// <summary>The application stopped during the run.</summary>
    Canceled = 10,

    /// <summary>An unexpected error interrupted the run.</summary>
    Failed = 11,
}

/// <summary>Result of one executed step.</summary>
public sealed record HealingStepResult
{
    /// <summary>Identifier of the run the step belongs to (<see cref="HealingRunResult.RunId"/>).</summary>
    public required string RunId { get; init; }

    /// <summary>Condition being healed.</summary>
    public required string ConditionName { get; init; }

    /// <summary>The healing group of the condition (<see cref="Conditions.ConditionBuilder.InGroup"/>), or <c>null</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Group { get; init; }

    /// <summary>Zero-based index of the step.</summary>
    public required int Step { get; init; }

    /// <summary>Step name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the action completed without throwing or timing out.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Verification outcome; <c>null</c> when the action did not succeed.</summary>
    public VerificationOutcome? Verification { get; init; }

    /// <summary>The exception thrown by the action, or a <see cref="TimeoutException"/>.</summary>
    public Exception? Error { get; init; }

    /// <summary>
    /// Whether the compensating action (<see cref="HealingPlanBuilder.UndoWith{TAction}"/>) completed; <c>null</c> when
    /// it did not run (no compensating action, or the step was healed or inconclusive).
    /// </summary>
    public bool? UndoSucceeded { get; init; }
}

/// <summary>Result of a complete healing run.</summary>
public sealed record HealingRunResult
{
    /// <summary>Identifier of the run, also on each of its <see cref="HealingStepResult"/>s.</summary>
    public required string RunId { get; init; }

    /// <summary>Condition being healed.</summary>
    public required string ConditionName { get; init; }

    /// <summary>
    /// The healing group of the condition when the run started (<see cref="Conditions.ConditionBuilder.InGroup"/>), or
    /// <c>null</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Group { get; init; }

    /// <summary>The instance that ran the plan (<see cref="HomeostatOptions.InstanceId"/>).</summary>
    public required string Instance { get; init; }

    /// <summary>How the run ended.</summary>
    public required HealingRunOutcome Outcome { get; init; }

    /// <summary>Executed steps, in order.</summary>
    public required IReadOnlyList<HealingStepResult> Steps { get; init; }

    /// <summary>When the run started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the run ended.</summary>
    public required DateTimeOffset CompletedAt { get; init; }
}
