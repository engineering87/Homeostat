using Homeostat.Signals;

namespace Homeostat.Conditions;

/// <summary>Point-in-time view of a condition.</summary>
public sealed record ConditionSnapshot
{
    /// <summary>Condition name.</summary>
    public required string Name { get; init; }

    /// <summary>Current status.</summary>
    public required ConditionStatus Status { get; init; }

    /// <summary>When the first triggering signal of the current episode arrived.</summary>
    public DateTimeOffset? PendingSince { get; init; }

    /// <summary>When the condition became active.</summary>
    public DateTimeOffset? ActiveSince { get; init; }

    /// <summary>Timestamp of the latest relevant signal.</summary>
    public DateTimeOffset? LastSignalAt { get; init; }

    /// <summary>The latest relevant signal.</summary>
    public Signal? LastSignal { get; init; }

    /// <summary>
    /// When clearing signals started, while an active condition waits for <see cref="ConditionBuilder.ClearAfter"/>.
    /// </summary>
    public DateTimeOffset? ClearingSince { get; init; }

    /// <summary>
    /// The condition that currently suppresses this one (one of the conditions listed with <see cref="ConditionBuilder.SuppressedBy"/> that is
    /// pending or active), if any. Filled in the snapshots returned by <see cref="IHomeostatControl.GetConditions"/> and
    /// passed to observers.
    /// </summary>
    public string? SuppressedBy { get; init; }
}
