namespace Homeostat.Healing;

/// <summary>Result of checking whether an action actually healed the condition.</summary>
/// <remarks>
/// Numbered from 1 on purpose: 0 (<c>default</c>) is deliberately not a member, so a value never set is caught as
/// undefined and never reads as a success.
/// </remarks>
public enum VerificationOutcome
{
    /// <summary>There is evidence that the condition cleared. The run stops successfully.</summary>
    Healed = 1,

    /// <summary>There is evidence that the condition still holds. The run escalates to the next step.</summary>
    NotHealed = 2,

    /// <summary>
    /// There is not enough evidence either way, for example because no new signal arrived.
    /// The run stops without escalating, since escalating blindly could make things worse.
    /// </summary>
    Inconclusive = 3,
}
