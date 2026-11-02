namespace Homeostat.Conditions;

/// <summary>Holds a tracker for every registered condition.</summary>
internal sealed class ConditionSet
{
    private readonly Dictionary<string, ConditionTracker> _byName;

    public ConditionSet(IEnumerable<ConditionDefinition> definitions)
    {
        Trackers = definitions.Select(d => new ConditionTracker(d)).ToArray();
        _byName = Trackers.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<ConditionTracker> Trackers { get; }

    /// <summary>
    /// Returns the first condition listed in <see cref="ConditionDefinition.SuppressedBy"/> that is pending or active.
    /// A pending cause counts, so a symptom with a shorter <c>SustainedFor</c> does not act before its cause activates.
    /// </summary>
    public string? FindActiveSuppressor(ConditionDefinition definition)
    {
        foreach (var name in definition.SuppressedBy)
        {
            if (_byName.TryGetValue(name, out var cause) && cause.Snapshot().Status != ConditionStatus.Inactive)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>Snapshot that also reports the active suppressor.</summary>
    public ConditionSnapshot Snapshot(ConditionTracker tracker)
        => tracker.Snapshot() with { SuppressedBy = FindActiveSuppressor(tracker.Definition) };
}
