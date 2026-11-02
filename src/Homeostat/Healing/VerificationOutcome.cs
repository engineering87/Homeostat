namespace Homeostat.Healing;

/// <summary>Result of checking whether an action actually healed the condition.</summary>
public enum VerificationOutcome
{
    /// <summary>There is evidence that the condition cleared. The run stops successfully.</summary>
    Healed = 0,

    /// <summary>There is evidence that the condition still holds. The run escalates to the next step.</summary>
    NotHealed = 1,

    /// <summary>
    /// There is not enough evidence either way, for example because no new signal arrived.
    /// The run stops without escalating, since escalating blindly could make things worse.
    /// </summary>
    Inconclusive = 2,
}
