namespace Homeostat.Healing;

/// <summary>
/// Decides whether a remediation action worked. The default verifier waits for the condition to
/// clear; implement this interface to use a synthetic probe or any other evidence instead.
/// </summary>
public interface IHealingVerifier
{
    /// <param name="context">The healing context, including when the action completed.</param>
    /// <param name="timeout">Maximum time the verifier should spend.</param>
    /// <param name="cancellationToken">Cancelled when the application stops.</param>
    Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken);
}
