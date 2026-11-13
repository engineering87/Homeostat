using Homeostat.Persistence;

namespace Homeostat.Guardrails;

/// <summary>
/// The state of one healing group on this instance (<see cref="IHomeostatControl.GetGroups"/>): its conditions, its own
/// pause and its guardrails. The use of its budget is read from the store with
/// <see cref="IHomeostatControl.GetGroupBudgetAsync"/>.
/// </summary>
/// <remarks>
/// A nominal record, not a positional one: users only read it, and properties can be added later without changing a
/// constructor or a deconstruction order.
/// </remarks>
public sealed record HealingGroupStatus
{
    /// <summary>Name of the group.</summary>
    public required string Name { get; init; }

    /// <summary>The conditions registered in the group on this instance, in registration order (none for a group only configured or paused).</summary>
    public required IReadOnlyList<string> Conditions { get; init; }

    /// <summary>
    /// Whether the group itself is paused: by <see cref="IHomeostatControl.PauseGroupAsync"/> or by one of its maintenance
    /// windows. The global pause, which pauses every group, is reported by <see cref="IHomeostatControl.IsPaused"/>.
    /// </summary>
    public required bool IsPaused { get; init; }

    /// <summary>Why the group itself is paused (who paused it, until when, or which maintenance window), or <c>null</c>.</summary>
    public string? PauseReason { get; init; }

    /// <summary>
    /// The group's pause as the store keeps it, or <c>null</c>. A timed pause stays here after its end until the group is
    /// resumed, but no longer applies (<see cref="IsPaused"/>).
    /// </summary>
    public GroupPauseState? Pause { get; init; }

    /// <summary>The group's concurrency limit (<see cref="HealingGroupOptions.MaxConcurrentHealings"/>), or <c>null</c>.</summary>
    public int? MaxConcurrentHealings { get; init; }

    /// <summary>The group's action budget (<see cref="HealingGroupOptions.MaxActionsPerWindow"/>), or <c>null</c> without one.</summary>
    public int? MaxActionsPerWindow { get; init; }

    /// <summary>The window of the group's action budget, or <c>null</c> without a budget.</summary>
    public TimeSpan? ActionBudgetWindow { get; init; }
}
