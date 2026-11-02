using Homeostat.Healing;

namespace Homeostat;

/// <summary>
/// Approves or rejects steps marked with <see cref="HealingPlanBuilder.RequireApproval"/>.
/// Expose it through your own tooling, or use the <c>Homeostat.AspNetCore</c> endpoints.
/// </summary>
public interface IHomeostatApprovals
{
    /// <summary>Requests currently waiting for an answer, oldest first.</summary>
    IReadOnlyList<PendingApproval> GetPending();

    /// <summary>Approves a pending request. Returns <c>false</c> if the request does not exist (any more).</summary>
    /// <param name="id">Request identifier.</param>
    /// <param name="approvedBy">Who approved it, recorded in logs and events.</param>
    bool Approve(string id, string? approvedBy = null);

    /// <summary>Rejects a pending request. Returns <c>false</c> if the request does not exist (any more).</summary>
    /// <param name="id">Request identifier.</param>
    /// <param name="rejectedBy">Who rejected it, recorded in logs and events.</param>
    bool Reject(string id, string? rejectedBy = null);
}
