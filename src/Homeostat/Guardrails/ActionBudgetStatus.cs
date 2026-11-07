namespace Homeostat.Guardrails;

/// <summary>Current use of the global action budget.</summary>
/// <remarks>
/// A nominal record, not a positional one: users only read it, and properties can be added later (a reset time, a
/// per-plan budget) without changing a constructor or a deconstruction order.
/// </remarks>
public sealed record ActionBudgetStatus
{
    /// <summary>Actions executed within the current window.</summary>
    public required int Used { get; init; }

    /// <summary>Maximum actions per window (<see cref="HomeostatOptions.MaxActionsPerWindow"/>).</summary>
    public required int Limit { get; init; }

    /// <summary>Length of the sliding window (<see cref="HomeostatOptions.ActionBudgetWindow"/>).</summary>
    public required TimeSpan Window { get; init; }

    /// <summary>Actions that can still run within the current window.</summary>
    public int Remaining => Math.Max(0, Limit - Used);

    /// <summary>Whether no further action can run until older actions leave the window.</summary>
    public bool IsExhausted => Used >= Limit;
}
