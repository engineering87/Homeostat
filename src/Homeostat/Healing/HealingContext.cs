using Homeostat.Conditions;
using Homeostat.Signals;

namespace Homeostat.Healing;

/// <summary>Information available to actions and verifiers during a healing run.</summary>
public sealed class HealingContext
{
    private readonly Func<ConditionSnapshot> _snapshot;

    internal HealingContext(
        string conditionName,
        Signal triggeringSignal,
        int step,
        string stepName,
        DateTimeOffset startedAt,
        IServiceProvider services,
        Func<ConditionSnapshot> snapshot)
    {
        ConditionName = conditionName;
        TriggeringSignal = triggeringSignal;
        Step = step;
        StepName = stepName;
        StartedAt = startedAt;
        Services = services;
        _snapshot = snapshot;
    }

    /// <summary>Name of the condition being healed.</summary>
    public string ConditionName { get; }

    /// <summary>The triggering signal that started this run: the activating signal, or the latest triggering signal for a retry after cooldown.</summary>
    public Signal TriggeringSignal { get; }

    /// <summary>Zero-based index of the current step in the plan's escalation chain.</summary>
    public int Step { get; }

    /// <summary>Name of the current step, as shown in logs and metrics.</summary>
    public string StepName { get; }

    /// <summary>When the current step started.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>When the current action completed; set before verification starts.</summary>
    public DateTimeOffset? ActionCompletedAt { get; internal set; }

    /// <summary>Hysteresis of the condition, used by the default verifier.</summary>
    internal TimeSpan ClearAfter { get; init; }

    /// <summary>Whether the plan runs once per cluster: its switches are then application switches.</summary>
    internal bool IsClusterPlan { get; init; }

    /// <summary>Services from the scope created for this step.</summary>
    public IServiceProvider Services { get; }

    /// <summary>Current state of the condition being healed.</summary>
    public ConditionSnapshot GetConditionState() => _snapshot();
}
