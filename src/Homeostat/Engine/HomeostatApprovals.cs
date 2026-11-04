using System.Collections.Concurrent;
using Homeostat.Healing;
using Homeostat.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Engine;

/// <summary>
/// Approval requests in the store, so any instance can answer them, and the waits of this instance's runs: an answer
/// given here completes the wait at once, one given elsewhere when the control state is next refreshed.
/// </summary>
internal sealed class HomeostatApprovals : IHomeostatApprovals
{
    private readonly ConcurrentDictionary<string, Entry> _waiting = new(StringComparer.Ordinal);
    private readonly SharedControl _shared;
    private readonly TimeProvider _timeProvider;
    private readonly string _instance;
    private readonly ILogger<HomeostatApprovals> _logger;

    public HomeostatApprovals(SharedControl shared, TimeProvider timeProvider, IOptions<HomeostatOptions> options, ILogger<HomeostatApprovals> logger)
    {
        _shared = shared;
        _timeProvider = timeProvider;
        _instance = options.Value.InstanceId;
        _logger = logger;
        _shared.Refreshed += (_, state) => Collect(state);
    }

    public async ValueTask<IReadOnlyList<PendingApproval>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        var state = await _shared.Store.GetControlStateAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        return state.Approvals.Where(a => a.Status is null && a.Request.ExpiresAt > now).Select(a => a.Request).OrderBy(r => r.RequestedAt).ToArray();
    }

    public ValueTask<bool> ApproveAsync(string id, string? approvedBy = null, CancellationToken cancellationToken = default)
        => ResolveAsync(id, ApprovalStatus.Approved, approvedBy, cancellationToken);

    public ValueTask<bool> RejectAsync(string id, string? rejectedBy = null, CancellationToken cancellationToken = default)
        => ResolveAsync(id, ApprovalStatus.Rejected, rejectedBy, cancellationToken);

    /// <summary>
    /// Records a request in the store; the returned task completes when someone answers it (on any instance), or when
    /// this instance expires or withdraws it.
    /// </summary>
    public async Task<Task<(ApprovalStatus Status, string? By)>> RegisterAsync(PendingApproval request, CancellationToken cancellationToken)
    {
        var entry = new Entry(request);
        _waiting[request.Id] = entry;
        try
        {
            await _shared.Store.AddApprovalAsync(new ApprovalRecord { Request = request, Instance = _instance }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _waiting.TryRemove(request.Id, out _);
            throw;
        }

        return entry.Decision.Task;
    }

    /// <summary>Marks the request as expired unless someone answered it first; the registered task tells which happened.</summary>
    public Task ExpireAsync(string id) => CloseAsync(id, ApprovalStatus.Expired);

    /// <summary>Withdraws a request because the application is stopping.</summary>
    public Task CancelAsync(string id) => CloseAsync(id, ApprovalStatus.Canceled);

    /// <summary>Withdraws every request of a condition that this instance waits for (the condition cleared).</summary>
    public void CancelFor(string condition)
    {
        foreach (var (id, entry) in _waiting)
        {
            if (string.Equals(entry.Request.ConditionName, condition, StringComparison.Ordinal))
            {
                _ = CloseAsync(id, ApprovalStatus.Canceled);
            }
        }
    }

    /// <summary>Removes an answered request from the store, once the run has used the answer.</summary>
    public async Task ForgetAsync(string id)
    {
        try
        {
            await _shared.Store.RemoveApprovalAsync(id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Harmless: an answered request is not listed as pending, and the stores drop old ones.
            _logger.LogDebug(ex, "Could not remove approval {ApprovalId} from the store.", id);
        }
    }

    private async ValueTask<bool> ResolveAsync(string id, ApprovalStatus status, string? by, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        // An expired request is no longer waited for, even if its instance could not record that in the store.
        var state = await _shared.Store.GetControlStateAsync(cancellationToken).ConfigureAwait(false);
        if (state.Approvals.FirstOrDefault(a => string.Equals(a.Request.Id, id, StringComparison.Ordinal)) is not { Status: null } pending
            || pending.Request.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            return false;
        }

        // Whoever answers first in the store wins: an answer racing with expiry is either applied or reported as not found.
        if (!await _shared.Store.TryResolveApprovalAsync(id, status, by, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (_waiting.TryRemove(id, out var entry))
        {
            entry.Decision.TrySetResult((status, by));
        }

        return true;
    }

    /// <summary>This instance ends its own wait: the store decides whether its answer or someone else's counts.</summary>
    private async Task CloseAsync(string id, ApprovalStatus status)
    {
        if (!_waiting.ContainsKey(id))
        {
            return;
        }

        try
        {
            if (await _shared.Store.TryResolveApprovalAsync(id, status, null, CancellationToken.None).ConfigureAwait(false))
            {
                Complete(id, status, null);
                return;
            }

            // Someone answered first: take that answer from the store.
            var state = await _shared.Store.GetControlStateAsync(CancellationToken.None).ConfigureAwait(false);
            if (state.Approvals.FirstOrDefault(a => a.Request.Id == id) is { Status: { } answer } record)
            {
                Complete(id, answer, record.ResolvedBy);
                return;
            }
        }
        catch (Exception ex)
        {
            // Without the store, nobody's answer can be trusted: the step does not run.
            _logger.LogWarning(ex, "Could not close approval {ApprovalId} in the store; treating it as {Status}.", id, status);
        }

        Complete(id, status, null);
    }

    private void Collect(ControlState state)
    {
        foreach (var record in state.Approvals)
        {
            if (record.Status is { } status && _waiting.ContainsKey(record.Request.Id))
            {
                Complete(record.Request.Id, status, record.ResolvedBy);
            }
        }
    }

    private void Complete(string id, ApprovalStatus status, string? by)
    {
        if (_waiting.TryRemove(id, out var entry))
        {
            entry.Decision.TrySetResult((status, by));
        }
    }

    private sealed class Entry(PendingApproval request)
    {
        public PendingApproval Request { get; } = request;

        public TaskCompletionSource<(ApprovalStatus Status, string? By)> Decision { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
