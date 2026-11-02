using System.Collections.Concurrent;
using Homeostat.Healing;

namespace Homeostat.Engine;

internal sealed class HomeostatApprovals : IHomeostatApprovals
{
    private readonly ConcurrentDictionary<string, Entry> _pending = new(StringComparer.Ordinal);

    public IReadOnlyList<PendingApproval> GetPending()
        => _pending.Values.Select(e => e.Request).OrderBy(r => r.RequestedAt).ToArray();

    public bool Approve(string id, string? approvedBy = null) => Resolve(id, approved: true, approvedBy);

    public bool Reject(string id, string? rejectedBy = null) => Resolve(id, approved: false, rejectedBy);

    /// <summary>Registers a request; the returned task completes when someone answers it or it expires.</summary>
    public Task<(ApprovalStatus Status, string? By)> Register(PendingApproval request)
    {
        var entry = new Entry(request);
        _pending[request.Id] = entry;
        return entry.Decision.Task;
    }

    /// <summary>
    /// Marks the request as expired unless someone answered it first; the registered task tells which happened.
    /// </summary>
    public void Expire(string id)
    {
        if (_pending.TryRemove(id, out var entry))
        {
            entry.Decision.TrySetResult((ApprovalStatus.Expired, null));
        }
    }

    /// <summary>Withdraws every pending request of a condition (it cleared).</summary>
    public void CancelFor(string condition)
    {
        foreach (var (id, entry) in _pending)
        {
            if (entry.Request.ConditionName == condition && _pending.TryRemove(id, out var removed))
            {
                removed.Decision.TrySetResult((ApprovalStatus.Canceled, null));
            }
        }
    }

    /// <summary>Withdraws a request because the application is stopping.</summary>
    public void Cancel(string id)
    {
        if (_pending.TryRemove(id, out var entry))
        {
            entry.Decision.TrySetResult((ApprovalStatus.Canceled, null));
        }
    }

    private bool Resolve(string id, bool approved, string? by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        // Whoever completes the task first wins: an answer racing with expiry is either applied or reported as not found.
        return _pending.TryRemove(id, out var entry)
            && entry.Decision.TrySetResult((approved ? ApprovalStatus.Approved : ApprovalStatus.Rejected, by));
    }

    private sealed class Entry(PendingApproval request)
    {
        public PendingApproval Request { get; } = request;

        public TaskCompletionSource<(ApprovalStatus Status, string? By)> Decision { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
