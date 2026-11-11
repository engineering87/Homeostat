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
        // Bounded and off the caller's thread, like every other store call (F1): a store that hangs must not hold the
        // request that lists the approvals.
        var state = await SharedControl.Bounded(() => _shared.Store.GetControlStateAsync(cancellationToken), SharedControl.ReadTimeout, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        return state.Approvals.Where(a => a.Status is null && a.Request.ExpiresAt > now).Select(a => a.Request).OrderBy(r => r.RequestedAt).ToArray();
    }

    public ValueTask<bool> ApproveAsync(string id, string? by = null, CancellationToken cancellationToken = default)
        => ResolveAsync(id, ApprovalStatus.Approved, by, cancellationToken);

    public ValueTask<bool> RejectAsync(string id, string? by = null, CancellationToken cancellationToken = default)
        => ResolveAsync(id, ApprovalStatus.Rejected, by, cancellationToken);

    /// <summary>
    /// Records a request in the store; the returned task completes when someone answers it (on any instance), or when
    /// this instance expires or withdraws it.
    /// </summary>
    public async Task<Task<(ApprovalStatus Status, string? By)>> RegisterAsync(PendingApproval request, CancellationToken cancellationToken)
    {
        var entry = new Entry(request);
        _waiting[request.Id] = entry;
        var record = new ApprovalRecord { Request = request, Instance = _instance };
        var adding = Task.Run(() => _shared.Store.AddApprovalAsync(record, cancellationToken).AsTask(), CancellationToken.None);
        try
        {
            await adding.WaitAsync(SharedControl.WriteTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _waiting.TryRemove(request.Id, out _);
            if (!adding.IsCompleted || adding.IsCompletedSuccessfully)
            {
                // The store may still write the request after the step gave up on it (or wrote it as the wait was
                // canceled): nobody would wait for it, and it would be listed as pending. Removed once the write ends.
                _ = RemoveLateAsync(request.Id, adding);
            }

            throw;
        }

        return entry.Decision.Task;
    }

    // How long a registration abandoned at its timeout is watched for a late write.
    internal static readonly TimeSpan LateWriteWait = TimeSpan.FromMinutes(5);

    // In the background: bounded, never throws.
    private async Task RemoveLateAsync(string id, Task adding)
    {
        try
        {
            await adding.WaitAsync(LateWriteWait).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Failed (nothing was written) or still not done (given up on: the stores drop old requests).
            _logger.LogDebug(ex, "Approval request {ApprovalId} was not written late.", id);
            return;
        }

        await ForgetAsync(id).ConfigureAwait(false);
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
                // Called on the engine loop (a condition cleared) and on configuration reloads: never wait there for
                // the store, which may block before returning its task.
                _ = Task.Run(() => CloseAsync(id, ApprovalStatus.Canceled), CancellationToken.None);
            }
        }
    }

    /// <summary>Removes an answered request from the store, once the run has used the answer.</summary>
    public async Task ForgetAsync(string id)
    {
        try
        {
            await SharedControl.Bounded(() => _shared.Store.RemoveApprovalAsync(id, CancellationToken.None), SharedControl.WriteTimeout, CancellationToken.None).ConfigureAwait(false);
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
        var state = await SharedControl.Bounded(() => _shared.Store.GetControlStateAsync(cancellationToken), SharedControl.ReadTimeout, cancellationToken).ConfigureAwait(false);
        if (state.Approvals.FirstOrDefault(a => string.Equals(a.Request.Id, id, StringComparison.Ordinal)) is not { Status: null } pending
            || pending.Request.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            return false;
        }

        // A request this instance made but no longer waits for (withdrawn while the store failed, so it is still pending
        // there; or its registration timed out): an answer would run nothing, so it is refused instead of reported as
        // given. Only this instance knows that its own runs stopped waiting.
        if (string.Equals(pending.Instance, _instance, StringComparison.Ordinal) && !_waiting.ContainsKey(id))
        {
            _logger.LogInformation("Approval {ApprovalId} was not answered: the run that asked for it no longer waits for it.", id.ReplaceLineEndings(" "));
            return false;
        }

        // Whoever answers first in the store wins: an answer racing with expiry is either applied or reported as not found.
        bool answered;
        try
        {
            answered = await SharedControl.Bounded(() => _shared.Store.TryResolveApprovalAsync(id, status, by, cancellationToken), SharedControl.WriteTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The store may still apply the answer: an approval the caller is told failed could then run the step. Read
            // the request once more to tell.
            answered = await WasAnsweredAsync(id, status, by, cancellationToken).ConfigureAwait(false);
        }

        if (!answered)
        {
            return false;
        }

        if (_waiting.TryRemove(id, out var entry))
        {
            entry.Decision.TrySetResult((status, by));
        }

        return true;
    }

    /// <summary>
    /// After an answer timed out: <c>true</c> if the store recorded this answer, <c>false</c> if it recorded another one
    /// (this one can no longer apply); a <see cref="TimeoutException"/> when the outcome is unknown (still pending, or the
    /// store does not answer), since the store may still apply it.
    /// </summary>
    private async Task<bool> WasAnsweredAsync(string id, ApprovalStatus status, string? by, CancellationToken cancellationToken)
    {
        ApprovalRecord? record;
        try
        {
            var state = await SharedControl.Bounded(() => _shared.Store.GetControlStateAsync(cancellationToken), SharedControl.ReadTimeout, cancellationToken).ConfigureAwait(false);
            record = state.Approvals.FirstOrDefault(a => string.Equals(a.Request.Id, id, StringComparison.Ordinal));
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unknown(ex);
        }

        if (record?.Status is not { } recorded)
        {
            throw Unknown(null);
        }

        return recorded == status && string.Equals(record.ResolvedBy, by, StringComparison.Ordinal);

        TimeoutException Unknown(Exception? inner) => new(
            $"The store did not confirm the answer to approval '{id.ReplaceLineEndings(" ")}' in time: the outcome is unknown, "
            + "and the store may still apply it. Read the pending approvals before answering again.", inner);
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
            if (await SharedControl.Bounded(() => _shared.Store.TryResolveApprovalAsync(id, status, null, CancellationToken.None), SharedControl.WriteTimeout, CancellationToken.None).ConfigureAwait(false))
            {
                Complete(id, status, null);
                return;
            }

            // Someone answered first: take that answer from the store.
            var state = await SharedControl.Bounded(() => _shared.Store.GetControlStateAsync(CancellationToken.None), SharedControl.ReadTimeout, CancellationToken.None).ConfigureAwait(false);
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
