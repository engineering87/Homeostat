namespace Homeostat.Healing;

/// <summary>
/// A remediation step. Implementations are resolved from dependency injection in a new scope
/// for every execution, so they can take any registered service in their constructor.
/// </summary>
/// <remarks>
/// Actions should be idempotent and quick: a healing run may execute the same action again later,
/// and every action is bounded by a timeout.
/// </remarks>
public interface IHealingAction
{
    /// <summary>Performs the remediation.</summary>
    /// <param name="context">The condition, the triggering signal and the step's services.</param>
    /// <param name="cancellationToken">Cancelled when the action times out or the application stops.</param>
    Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken);
}
