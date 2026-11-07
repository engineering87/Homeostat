using Homeostat.Healing;

namespace Homeostat;

/// <summary>
/// Approves or rejects steps marked with <see cref="HealingPlanBuilder.RequireApproval"/>.
/// Expose it through your own tooling, or use the <c>Homeostat.AspNetCore</c> endpoints.
/// </summary>
/// <remarks>
/// Requests are kept in the store: with a shared store, any instance lists them and can answer them, and the answer
/// reaches the waiting instance within <see cref="HomeostatOptions.ControlRefreshInterval"/>.
/// </remarks>
public interface IHomeostatApprovals
{
    /// <summary>Requests currently waiting for an answer, oldest first, from every instance.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<IReadOnlyList<PendingApproval>> GetPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>Approves a pending request. Returns <c>false</c> if the request does not exist or was already answered.</summary>
    /// <param name="id">Request identifier.</param>
    /// <param name="by">Who approved it, recorded in logs and events.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<bool> ApproveAsync(string id, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>Rejects a pending request. Returns <c>false</c> if the request does not exist or was already answered.</summary>
    /// <param name="id">Request identifier.</param>
    /// <param name="by">Who rejected it, recorded in logs and events.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<bool> RejectAsync(string id, string? by = null, CancellationToken cancellationToken = default);
}
