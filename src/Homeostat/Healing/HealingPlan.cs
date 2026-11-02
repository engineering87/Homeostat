namespace Homeostat.Healing;

/// <summary>
/// Ordered escalation chain of steps for one condition, with its verification and pacing rules.
/// </summary>
internal sealed class HealingPlan
{
    internal HealingPlan(
        string conditionName,
        IReadOnlyList<HealingStep> steps,
        Type? verifierType,
        TimeSpan? verificationTimeout,
        TimeSpan? actionTimeout,
        TimeSpan? cooldown,
        PlanScope scope,
        double cooldownBackoff,
        TimeSpan? maxCooldown)
    {
        Scope = scope;
        CooldownBackoff = cooldownBackoff;
        MaxCooldown = maxCooldown;
        ConditionName = conditionName;
        Steps = steps;
        VerifierType = verifierType;
        VerificationTimeout = verificationTimeout;
        ActionTimeout = actionTimeout;
        Cooldown = cooldown;
    }

    /// <summary>
    /// When set, the steps are reordered for each run by how often they healed this condition (see
    /// <see cref="HealingPlanBuilder.Adaptive"/>), once they have been tried this many times; <c>null</c> keeps the
    /// declared order.
    /// </summary>
    public int? AdaptiveMinimumAttempts { get; internal init; }

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

    /// <summary>Where the plan runs: on every instance that sees the condition, or once across the cluster.</summary>
    public PlanScope Scope { get; }

    /// <summary>Factor applied to the cooldown after each run in a row that did not heal; 1 means no backoff.</summary>
    public double CooldownBackoff { get; }

    /// <summary>Upper bound of the cooldown when <see cref="CooldownBackoff"/> is above 1.</summary>
    public TimeSpan? MaxCooldown { get; }
}
