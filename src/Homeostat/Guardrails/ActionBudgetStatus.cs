using System.Text.Json.Serialization;

namespace Homeostat.Guardrails;

/// <summary>Current use of the global action budget, or of the budget of a healing group.</summary>
/// <remarks>
/// A nominal record, not a positional one: users only read it, and properties can be added later (a reset time, a
/// per-plan budget) without changing a constructor or a deconstruction order.
/// </remarks>
public sealed record ActionBudgetStatus
{
    /// <summary>
    /// The healing group whose budget this is (<see cref="IHomeostatControl.GetGroupBudgetAsync"/>), or <c>null</c> for
    /// the global budget.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Group { get; init; }

    /// <summary>Actions executed within the current window.</summary>
    public required int Used { get; init; }

    /// <summary>Maximum actions per window (<see cref="HomeostatOptions.MaxActionsPerWindow"/>, or <see cref="HealingGroupOptions.MaxActionsPerWindow"/>).</summary>
    public required int Limit { get; init; }

    /// <summary>Length of the sliding window (<see cref="HomeostatOptions.ActionBudgetWindow"/>, or the group's window).</summary>
    public required TimeSpan Window { get; init; }

    /// <summary>Actions that can still run within the current window.</summary>
    public int Remaining => Math.Max(0, Limit - Used);

    /// <summary>Whether no further action can run until older actions leave the window.</summary>
    public bool IsExhausted => Used >= Limit;
}
