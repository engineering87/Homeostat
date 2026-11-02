using Homeostat.Signals;

namespace Homeostat.Persistence;

/// <summary>Kinds of <see cref="ConditionEventRecord"/>.</summary>
public enum ConditionEventKind
{
    /// <summary>The condition became active.</summary>
    Activated,

    /// <summary>The condition cleared.</summary>
    Cleared,
}

/// <summary>
/// A condition becoming active or clearing, as kept in the condition history (see
/// <see cref="HomeostatOptions.ConditionHistorySize"/>): serializable, with the signal that caused it.
/// </summary>
public sealed record ConditionEventRecord
{
    /// <summary>Condition name.</summary>
    public required string ConditionName { get; init; }

    /// <summary>What happened.</summary>
    public required ConditionEventKind Kind { get; init; }

    /// <summary>When it happened.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Instance where it happened (<see cref="HomeostatOptions.InstanceId"/>).</summary>
    public string Instance { get; init; } = string.Empty;

    /// <summary>Source of the signal that caused it.</summary>
    public string? Source { get; init; }

    /// <summary>Severity of the signal that caused it.</summary>
    public SignalSeverity? Severity { get; init; }

    /// <summary>Value of the signal that caused it, if any.</summary>
    public double? Value { get; init; }

    /// <summary>Message of the signal that caused it, if any, cut to 300 characters.</summary>
    public string? Message { get; init; }
}
