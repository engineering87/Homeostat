namespace Homeostat.Guardrails;

/// <summary>Current use of the global action budget.</summary>
/// <param name="Used">Actions executed within the current window.</param>
/// <param name="Limit">Maximum actions per window (<see cref="HomeostatOptions.MaxActionsPerWindow"/>).</param>
/// <param name="Window">Length of the sliding window (<see cref="HomeostatOptions.ActionBudgetWindow"/>).</param>
public sealed record ActionBudgetStatus(int Used, int Limit, TimeSpan Window)
{
    /// <summary>Actions that can still run within the current window.</summary>
    public int Remaining => Math.Max(0, Limit - Used);

    /// <summary>Whether no further action can run until older actions leave the window.</summary>
    public bool IsExhausted => Used >= Limit;
}
