using Homeostat.Conditions;
using Homeostat.Healing;

namespace Homeostat.Presets;

/// <summary>
/// One condition of a starter preset (<c>AddWebApiDefaults</c>, <c>AddWorkerDefaults</c>): a name, the signal it
/// watches and how long the problem must last. Presets only observe; give a condition a plan with <see cref="Heal"/>
/// once its activations in the logs look right.
/// </summary>
public sealed class DefaultCondition
{
    private readonly List<Action<ConditionBuilder>> _configure = [];
    private TimeSpan _sustainedFor;

    internal DefaultCondition(string name, string source, TimeSpan sustainedFor, bool enabled)
    {
        Name = name;
        Source = source;
        _sustainedFor = sustainedFor;
        Enabled = enabled;
    }

    /// <summary>Name of the condition, used in logs, metrics, endpoints and <c>SuppressedBy</c>.</summary>
    public string Name { get; }

    /// <summary>The signal source the condition watches.</summary>
    public string Source { get; }

    /// <summary>Whether the preset registers the condition and the detector behind it.</summary>
    public bool Enabled { get; set; }

    /// <summary>How long the problem must last before the condition becomes active.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan SustainedFor
    {
        get => _sustainedFor;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            _sustainedFor = value;
        }
    }

    /// <summary>
    /// Adds settings to the condition, applied after the preset's own: rules such as <c>ClearAfter</c> or
    /// <c>WhenSeverityAtLeast</c>, suppressions, or a plan.
    /// </summary>
    /// <param name="configure">Changes the condition.</param>
    /// <returns>This condition.</returns>
    public DefaultCondition Configure(Action<ConditionBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configure.Add(configure);
        return this;
    }

    /// <summary>Gives the condition a healing plan (see <see cref="ConditionBuilder.Heal"/>).</summary>
    /// <param name="configure">Adds the steps.</param>
    /// <returns>This condition.</returns>
    public DefaultCondition Heal(Action<HealingPlanBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return Configure(c => c.Heal(configure));
    }

    internal void Apply(ConditionBuilder condition, IEnumerable<string> suppressedBy)
    {
        condition.ForSource(Source).SustainedFor(SustainedFor);
        var causes = suppressedBy.ToArray();
        if (causes.Length > 0)
        {
            condition.SuppressedBy(causes);
        }

        foreach (var configure in _configure)
        {
            configure(condition);
        }
    }
}
