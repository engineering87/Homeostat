using Homeostat.Healing;

namespace Homeostat.Persistence;

/// <summary>
/// The operational state that every instance must see alike: the pause, the switches shared by the application and the
/// approval requests. Kept in the store, so with a shared store (for example <c>Homeostat.Redis</c>) a pause, a switch
/// or an answer to an approval given on one instance applies to all of them, and survives restarts.
/// </summary>
public sealed class ControlState
{
    /// <summary>A state with nothing paused, no switch on and no request.</summary>
    public static ControlState Empty { get; } = new();

    /// <summary>
    /// How many healing groups can be paused at once (<see cref="GroupPauses"/>): a store refuses the pause of one more
    /// group with an <see cref="InvalidOperationException"/>, so the control state stays small. Resuming a group removes
    /// its pause, timed ones included.
    /// </summary>
    public const int MaxGroupPauses = 256;

    /// <summary>The pause, or <c>null</c> when Homeostat is not paused (maintenance windows are not stored).</summary>
    public PauseState? Pause { get; init; }

    /// <summary>
    /// The pauses of healing groups (<see cref="IHomeostatControl.PauseGroupAsync"/>), at most one per group and at most
    /// <see cref="MaxGroupPauses"/>, ordered by group name. A timed pause stays listed after its end until the group is
    /// resumed; it no longer applies.
    /// </summary>
    public IReadOnlyList<GroupPauseState> GroupPauses { get; init; } = [];

    /// <summary>The switches turned on for the whole application.</summary>
    public IReadOnlyList<SwitchState> Switches { get; init; } = [];

    /// <summary>Approval requests, pending or answered but not yet collected by the instance that waits for them.</summary>
    public IReadOnlyList<ApprovalRecord> Approvals { get; init; } = [];
}

/// <summary>A pause of every healing plan (or, in <see cref="IHomeostatStore.TrySetGroupPauseAsync"/>, of the plans of a group).</summary>
public sealed class PauseState
{
    /// <summary>When the pause ends by itself, or <c>null</c> for a pause that lasts until it is resumed.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Who paused, when known.</summary>
    public string? By { get; init; }

    /// <summary>When the pause was set.</summary>
    public required DateTimeOffset At { get; init; }
}

/// <summary>
/// A pause of the healing plans of one healing group (<see cref="Conditions.ConditionBuilder.InGroup"/>): the same fields
/// as <see cref="PauseState"/>, with the group.
/// </summary>
public sealed class GroupPauseState
{
    /// <summary>The paused group.</summary>
    public required string Group { get; init; }

    /// <summary>When the pause ends by itself, or <c>null</c> for a pause that lasts until the group is resumed.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Who paused, when known.</summary>
    public string? By { get; init; }

    /// <summary>When the pause was set.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>The pause of <paramref name="group"/> described by <paramref name="pause"/>.</summary>
    /// <param name="group">The group.</param>
    /// <param name="pause">The pause.</param>
    public static GroupPauseState From(string group, PauseState pause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(pause);
        return new GroupPauseState { Group = group, Until = pause.Until, By = pause.By, At = pause.At };
    }

    /// <summary>The same pause without its group.</summary>
    public PauseState ToPause() => new() { Until = Until, By = By, At = At };
}

/// <summary>A switch that is on for the whole application.</summary>
public sealed class SwitchState
{
    /// <summary>Name of the switch.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// The condition whose cluster plan turned the switch on, so it is turned off when that condition clears; <c>null</c>
    /// for a switch turned on by a person or by code, which stays on until turned off.
    /// </summary>
    public string? Condition { get; init; }

    /// <summary>
    /// The instance whose cluster plan turned the switch on: it turns the switch off when the condition clears there.
    /// <c>null</c> for a switch turned on by a person or by code.
    /// </summary>
    public string? Instance { get; init; }

    /// <summary>Who turned it on, when known.</summary>
    public string? By { get; init; }

    /// <summary>When it was turned on.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// For a switch a cluster plan turned on: when <see cref="Instance"/> last confirmed it still holds it. A switch not
    /// renewed for a while (the instance stopped or crashed) is turned off by any instance where its condition is not
    /// active.
    /// </summary>
    public DateTimeOffset? RenewedAt { get; init; }

    /// <summary>
    /// For a switch a cluster plan turned on: how often <see cref="Instance"/> renews its claim. Other instances wait
    /// for at least four of these periods without a renewal before they treat the claim as abandoned, so an instance
    /// that renews less often than they would keeps its switch. <c>null</c> in records written before it existed: the
    /// observing instance then uses its own period.
    /// </summary>
    public TimeSpan? RenewInterval { get; init; }
}

/// <summary>An approval request as the store keeps it.</summary>
public sealed class ApprovalRecord
{
    /// <summary>The request.</summary>
    public required PendingApproval Request { get; init; }

    /// <summary>The answer, or <c>null</c> while the request is pending.</summary>
    public ApprovalStatus? Status { get; init; }

    /// <summary>Who answered, when known.</summary>
    public string? ResolvedBy { get; init; }

    /// <summary>The instance whose healing run waits for the answer.</summary>
    public required string Instance { get; init; }
}
