namespace Homeostat.Diagnostics;

/// <summary>
/// What Homeostat exercised in a period: which conditions activated, which plans ran and how they ended, which steps
/// never ran. After a game day (chaos turned on with switches), it shows what the experiments covered and what is
/// still untested.
/// </summary>
public sealed record CoverageReport
{
    /// <summary>Start of the period.</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>When the report was made.</summary>
    public required DateTimeOffset Until { get; init; }

    /// <summary>One entry per condition.</summary>
    public required IReadOnlyList<ConditionCoverage> Conditions { get; init; }

    /// <summary>
    /// Whether the run history or the condition history does not reach back to <see cref="Since"/>: they keep the
    /// latest <see cref="HomeostatOptions.RunHistorySize"/> runs and <see cref="HomeostatOptions.ConditionHistorySize"/>
    /// events, and older entries of the period are not counted.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>Conditions that never activated in the period.</summary>
    public IReadOnlyList<string> NeverActivated => [.. Conditions.Where(c => c.Activations == 0).Select(c => c.Name)];

    /// <summary>Conditions with a plan that never ran in the period.</summary>
    public IReadOnlyList<string> PlansNeverRun => [.. Conditions.Where(c => c.HasPlan && c.Runs == 0).Select(c => c.Name)];

    /// <summary>
    /// Share of plan steps that completed at least once, from 0 to 1; 1 when there are no plans. Steps that were tried
    /// but always failed (<see cref="ConditionCoverage.StepsFailed"/>) do not count as covered.
    /// </summary>
    public double StepCoverage
    {
        get
        {
            var total = Conditions.Sum(c => c.StepsRun.Count + c.StepsFailed.Count + c.StepsNeverRun.Count);
            return total == 0 ? 1 : (double)Conditions.Sum(c => c.StepsRun.Count) / total;
        }
    }
}

/// <summary>Coverage of one condition.</summary>
public sealed record ConditionCoverage
{
    /// <summary>Condition name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the condition has a healing plan.</summary>
    public required bool HasPlan { get; init; }

    /// <summary>
    /// Times it became active in the period, from the condition history: with the file and Redis stores, on every instance
    /// sharing the store and across restarts; otherwise on this instance, since it started.
    /// </summary>
    public required int Activations { get; init; }

    /// <summary>Runs of its plan in the period, from the run history (with a shared store, of every instance).</summary>
    public required int Runs { get; init; }

    /// <summary>Runs by outcome.</summary>
    public required IReadOnlyDictionary<string, int> Outcomes { get; init; }

    /// <summary>Plan steps whose action completed at least once.</summary>
    public required IReadOnlyList<string> StepsRun { get; init; }

    /// <summary>Plan steps that were tried but whose action never completed.</summary>
    public required IReadOnlyList<string> StepsFailed { get; init; }

    /// <summary>
    /// Plan steps that were never tried: escalation steps that the earlier steps always made unnecessary, or untested ones.
    /// </summary>
    public required IReadOnlyList<string> StepsNeverRun { get; init; }

    /// <summary>End of the latest run in the period.</summary>
    public DateTimeOffset? LastRunAt { get; init; }
}
