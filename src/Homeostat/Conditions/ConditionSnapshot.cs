using Homeostat.Signals;

namespace Homeostat.Conditions;

/// <summary>Point-in-time view of a condition.</summary>
/// <param name="Name">Condition name.</param>
/// <param name="Status">Current status.</param>
/// <param name="PendingSince">When the first triggering signal of the current episode arrived.</param>
/// <param name="ActiveSince">When the condition became active.</param>
/// <param name="LastSignalAt">Timestamp of the latest relevant signal.</param>
/// <param name="LastSignal">The latest relevant signal.</param>
public sealed record ConditionSnapshot(
    string Name,
    ConditionStatus Status,
    DateTimeOffset? PendingSince,
    DateTimeOffset? ActiveSince,
    DateTimeOffset? LastSignalAt,
    Signal? LastSignal)
{
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
