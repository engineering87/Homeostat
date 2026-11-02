namespace Homeostat;

/// <summary>
/// What Homeostat exercised in a period: which conditions activated, which plans ran and how they ended, which steps
/// never ran. After a game day (chaos turned on with switches), it shows what the experiments covered and what is
/// still untested.
/// </summary>
/// <param name="Since">Start of the period.</param>
/// <param name="Until">When the report was made.</param>
/// <param name="Conditions">One entry per condition.</param>
/// <param name="Truncated">
/// Whether the run history does not reach back to <paramref name="Since"/>: it keeps the latest
/// <see cref="HomeostatOptions.RunHistorySize"/> runs, and older runs of the period are not counted.
/// </param>
public sealed record CoverageReport(
    DateTimeOffset Since, DateTimeOffset Until, IReadOnlyList<ConditionCoverage> Conditions, bool Truncated = false)
{
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
/// <param name="Name">Condition name.</param>
/// <param name="HasPlan">Whether the condition has a healing plan.</param>
/// <param name="Activations">Times it became active in the period, on this instance (since it started).</param>
/// <param name="Runs">Runs of its plan in the period, from the run history (with a shared store, of every instance).</param>
/// <param name="Outcomes">Runs by outcome.</param>
/// <param name="StepsRun">Plan steps whose action completed at least once.</param>
/// <param name="StepsFailed">Plan steps that were tried but whose action never completed.</param>
/// <param name="StepsNeverRun">Plan steps that were never tried: escalation steps that the earlier steps always made unnecessary, or untested ones.</param>
/// <param name="LastRunAt">End of the latest run in the period.</param>
public sealed record ConditionCoverage(
    string Name,
    bool HasPlan,
    int Activations,
    int Runs,
    IReadOnlyDictionary<string, int> Outcomes,
    IReadOnlyList<string> StepsRun,
    IReadOnlyList<string> StepsFailed,
    IReadOnlyList<string> StepsNeverRun,
    DateTimeOffset? LastRunAt);
