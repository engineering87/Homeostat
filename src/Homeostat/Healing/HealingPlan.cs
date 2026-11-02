namespace Homeostat.Healing;

/// <summary>
/// Ordered escalation chain of steps for one condition, with its verification and pacing rules.
/// </summary>
public sealed class HealingPlan
{
    internal HealingPlan(
        string conditionName,
        IReadOnlyList<HealingStep> steps,
        Type? verifierType,
        TimeSpan? verificationTimeout,
        TimeSpan? actionTimeout,
        TimeSpan? cooldown)
    {
        ConditionName = conditionName;
        Steps = steps;
        VerifierType = verifierType;
        VerificationTimeout = verificationTimeout;
        ActionTimeout = actionTimeout;
        Cooldown = cooldown;
    }

    /// <summary>Name of the condition this plan heals.</summary>
    public string ConditionName { get; }

    /// <summary>Steps, tried in order until one is verified as healed or the run stops (inconclusive, budget, pause).</summary>
    public IReadOnlyList<HealingStep> Steps { get; }

    /// <summary>Custom verifier; <c>null</c> means <see cref="ConditionClearedVerifier"/>.</summary>
    public Type? VerifierType { get; }

    /// <summary><c>null</c> means <see cref="HomeostatOptions.DefaultVerificationTimeout"/>.</summary>
    public TimeSpan? VerificationTimeout { get; }

    /// <summary><c>null</c> means <see cref="HomeostatOptions.DefaultActionTimeout"/>.</summary>
    public TimeSpan? ActionTimeout { get; }

    /// <summary><c>null</c> means <see cref="HomeostatOptions.DefaultCooldown"/>.</summary>
    public TimeSpan? Cooldown { get; }
}
