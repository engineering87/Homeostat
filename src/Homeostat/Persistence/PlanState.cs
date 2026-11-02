namespace Homeostat.Persistence;

/// <summary>Durable state of a healing plan, kept by an <see cref="IHomeostatStore"/>.</summary>
public sealed record PlanState
{
    /// <summary>State of a plan that never ran.</summary>
    public static PlanState Empty { get; } = new();

    /// <summary>When the last run ended; drives the cooldown.</summary>
    public DateTimeOffset? LastRunEndedAt { get; init; }

    /// <summary>Runs in a row that did not heal; drives the cooldown backoff.</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>Whether the plan is held after a rejected approval, until the condition clears.</summary>
    public bool Held { get; init; }

    /// <summary>Instance that ran the plan last (<see cref="HomeostatOptions.InstanceId"/>).</summary>
    public string? LastRunBy { get; init; }
}

/// <summary>How an update changes <see cref="PlanState.ConsecutiveFailures"/>.</summary>
public enum FailureCountChange
{
    /// <summary>Leave the count as it is.</summary>
    Keep = 0,

    /// <summary>Add one.</summary>
    Increment = 1,

    /// <summary>Set it to zero.</summary>
    Reset = 2,
}

/// <summary>
/// A change to a plan's state that a store applies <b>atomically</b>: fields left <c>null</c> are not touched, so
/// two instances updating different fields do not overwrite each other.
/// </summary>
public sealed record PlanStateUpdate
{
    /// <summary>New end of the last run, or <c>null</c> to keep it.</summary>
    public DateTimeOffset? LastRunEndedAt { get; init; }

    /// <summary>New last runner, or <c>null</c> to keep it.</summary>
    public string? LastRunBy { get; init; }

    /// <summary>How to change the failure count.</summary>
    public FailureCountChange Failures { get; init; }

    /// <summary>New hold flag, or <c>null</c> to keep it.</summary>
    public bool? Held { get; init; }

    /// <summary>
    /// Precondition: when set, the update applies only if <see cref="PlanState.LastRunBy"/> equals it (otherwise the
    /// state is returned unchanged).
    /// </summary>
    public string? OnlyIfLastRunBy { get; init; }

    /// <summary>Reference semantics of the update, for store implementations.</summary>
    /// <param name="state">The current state.</param>
    public PlanState ApplyTo(PlanState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (OnlyIfLastRunBy is not null && !string.Equals(state.LastRunBy, OnlyIfLastRunBy, StringComparison.Ordinal))
        {
            return state;
        }

        return state with
        {
            LastRunEndedAt = LastRunEndedAt ?? state.LastRunEndedAt,
            LastRunBy = LastRunBy ?? state.LastRunBy,
            ConsecutiveFailures = Failures switch
            {
                FailureCountChange.Increment => state.ConsecutiveFailures + 1,
                FailureCountChange.Reset => 0,
                _ => state.ConsecutiveFailures,
            },
            Held = Held ?? state.Held,
        };
    }
}
