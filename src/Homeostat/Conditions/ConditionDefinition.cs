using Homeostat.Healing;
using Homeostat.Signals;

namespace Homeostat.Conditions;

/// <summary>
/// Immutable description of a condition: which signals matter, when it triggers, when it clears,
/// how long it must hold and which plan heals it. Build it with <see cref="ConditionBuilder"/>.
/// </summary>
internal sealed class ConditionDefinition
{
    internal ConditionDefinition(
        string name,
        Func<Signal, bool> isRelevant,
        Func<Signal, bool> isTriggered,
        Func<Signal, bool> isCleared,
        TimeSpan sustainFor,
        TimeSpan clearAfter,
        IReadOnlyList<string> suppressedBy,
        HealingPlan? plan,
        Action<Signal>? record = null,
        CompositeMode? composite = null,
        IReadOnlyList<string>? dependsOn = null,
        IReadOnlyList<SeasonalBaseline>? baselines = null,
        string? source = null)
    {
        Source = source;
        Baselines = baselines ?? [];
        Record = record;
        Composite = composite;
        DependsOn = dependsOn ?? [];
        Name = name;
        IsRelevant = isRelevant;
        IsTriggered = isTriggered;
        IsCleared = isCleared;
        SustainFor = sustainFor;
        ClearAfter = clearAfter;
        SuppressedBy = suppressedBy;
        Plan = plan;
    }

    /// <summary>Unique name of the condition, used in logs, metrics and endpoints.</summary>
    public string Name { get; }

    /// <summary>The only source this condition listens to, or <c>null</c> when a custom filter decides alone.</summary>
    internal string? Source { get; }

    /// <summary>Signals for which this returns <c>false</c> are ignored by the condition.</summary>
    internal Func<Signal, bool> IsRelevant { get; }

    /// <summary>Returns <c>true</c> for relevant signals that show the problem.</summary>
    internal Func<Signal, bool> IsTriggered { get; }

    /// <summary>Returns <c>true</c> for relevant signals that show recovery.</summary>
    internal Func<Signal, bool> IsCleared { get; }

    /// <summary>How long triggering signals must persist before the condition becomes active.</summary>
    public TimeSpan SustainFor { get; }

    /// <summary>How long clearing signals must persist before an active condition clears.</summary>
    public TimeSpan ClearAfter { get; }

    /// <summary>Conditions that, while active, prevent this condition's plan from running.</summary>
    public IReadOnlyList<string> SuppressedBy { get; }

    /// <summary>The healing plan; <c>null</c> for a condition that is only observed.</summary>
    public HealingPlan? Plan { get; }

    /// <summary>For a composite condition, how it combines <see cref="DependsOn"/>; <c>null</c> otherwise.</summary>
    public CompositeMode? Composite { get; }

    /// <summary>Conditions a composite condition is computed from.</summary>
    public IReadOnlyList<string> DependsOn { get; }

    /// <summary>Feeds stateful rules (trend, anomaly, occurrences) with each relevant signal, before evaluation.</summary>
    internal Action<Signal>? Record { get; }

    /// <summary>Baselines of seasonal rules, kept across restarts by the baseline keeper.</summary>
    internal IReadOnlyList<SeasonalBaseline> Baselines { get; }
}
