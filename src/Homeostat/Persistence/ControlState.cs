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

    /// <summary>The pause, or <c>null</c> when Homeostat is not paused (maintenance windows are not stored).</summary>
    public PauseState? Pause { get; init; }

    /// <summary>The switches turned on for the whole application.</summary>
    public IReadOnlyList<SwitchState> Switches { get; init; } = [];

    /// <summary>Approval requests, pending or answered but not yet collected by the instance that waits for them.</summary>
    public IReadOnlyList<ApprovalRecord> Approvals { get; init; } = [];
}

/// <summary>A pause of every healing plan.</summary>
public sealed class PauseState
{
    /// <summary>When the pause ends by itself, or <c>null</c> for a pause that lasts until it is resumed.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Who paused, when known.</summary>
    public string? By { get; init; }

    /// <summary>When the pause was set.</summary>
    public required DateTimeOffset At { get; init; }
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
