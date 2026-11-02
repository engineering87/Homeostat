using Homeostat.Healing;
using Homeostat.Signals;

namespace Homeostat.Conditions;

/// <summary>
/// Immutable description of a condition: which signals matter, when it triggers, when it clears,
/// how long it must hold and which plan heals it. Build it with <see cref="ConditionBuilder"/>.
/// </summary>
public sealed class ConditionDefinition
{
    internal ConditionDefinition(
        string name,
        Func<Signal, bool> isRelevant,
        Func<Signal, bool> isTriggered,
        Func<Signal, bool> isCleared,
        TimeSpan sustainFor,
        TimeSpan clearAfter,
        IReadOnlyList<string> suppressedBy,
        HealingPlan? plan)
    {
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

    /// <summary>Signals for which this returns <c>false</c> are ignored by the condition.</summary>
    public Func<Signal, bool> IsRelevant { get; }

    /// <summary>Returns <c>true</c> for relevant signals that show the problem.</summary>
    public Func<Signal, bool> IsTriggered { get; }

    /// <summary>Returns <c>true</c> for relevant signals that show recovery.</summary>
    public Func<Signal, bool> IsCleared { get; }

    /// <summary>How long triggering signals must persist before the condition becomes active.</summary>
    public TimeSpan SustainFor { get; }

    /// <summary>How long clearing signals must persist before an active condition clears.</summary>
    public TimeSpan ClearAfter { get; }

    /// <summary>Conditions that, while active, prevent this condition's plan from running.</summary>
    public IReadOnlyList<string> SuppressedBy { get; }

    /// <summary>The healing plan; <c>null</c> for a condition that is only observed.</summary>
    public HealingPlan? Plan { get; }
}
