using Homeostat.Healing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homeostat.Persistence;

/// <summary>
/// The file store of <c>UseFileStore</c>, opened on first use instead of when the services are built. A file that
/// cannot be opened (in use by the previous process during a restart, a read-only volume) never stops the application:
/// every store call fails, so Homeostat fails closed and does not act, and opening is tried again every few seconds.
/// </summary>
internal sealed class OpeningFileStore : IHomeostatStore, IDisposable
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);

    private readonly string _path;
    private readonly FileStoreOptions _options;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private FileHomeostatStore? _store;
    private Exception? _failure;
    private long _nextAttempt;
    private bool _disposed;

    public OpeningFileStore(string path, FileStoreOptions options, ILogger<OpeningFileStore>? logger = null)
    {
        _path = path;
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public bool SupportsConditionHistory => true;

    public bool SupportsDocuments => true;

    public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => Store().GetPlanStateAsync(key, cancellationToken);

    public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) => Store().UpdatePlanStateAsync(key, update, cancellationToken);

    public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => Store().TryConsumeBudgetAsync(now, limit, window, cancellationToken);

    public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => Store().GetBudgetUsedAsync(now, window, cancellationToken);

    public ValueTask<bool> TryConsumeGroupBudgetAsync(string group, DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => Store().TryConsumeGroupBudgetAsync(group, now, limit, window, cancellationToken);

    public ValueTask<int> GetGroupBudgetUsedAsync(string group, DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => Store().GetGroupBudgetUsedAsync(group, now, window, cancellationToken);

    public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => Store().AppendRunAsync(run, maxRuns, cancellationToken);

    public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) => Store().GetRecentRunsAsync(maxRuns, cancellationToken);

    public ValueTask AppendConditionEventAsync(ConditionEventRecord conditionEvent, int maxEvents, CancellationToken cancellationToken = default) => Store().AppendConditionEventAsync(conditionEvent, maxEvents, cancellationToken);

    public ValueTask<IReadOnlyList<ConditionEventRecord>> GetConditionEventsAsync(int maxEvents, CancellationToken cancellationToken = default) => Store().GetConditionEventsAsync(maxEvents, cancellationToken);

    public ValueTask<string?> GetDocumentAsync(string key, CancellationToken cancellationToken = default) => Store().GetDocumentAsync(key, cancellationToken);

    public ValueTask SetDocumentAsync(string key, string document, CancellationToken cancellationToken = default) => Store().SetDocumentAsync(key, document, cancellationToken);

    public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) => Store().GetControlStateAsync(cancellationToken);

    public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => Store().TrySetPauseAsync(pause, decidedAt, cancellationToken);

    public ValueTask<bool> TrySetGroupPauseAsync(string group, PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => Store().TrySetGroupPauseAsync(group, pause, decidedAt, cancellationToken);

    public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => Store().TrySetSwitchAsync(name, state, decidedAt, cancellationToken);

    public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => Store().TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken);

    public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) => Store().TryClaimSwitchAsync(state, cancellationToken);

    public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) => Store().AddApprovalAsync(approval, cancellationToken);

    public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default) => Store().TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken);

    public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) => Store().RemoveApprovalAsync(id, cancellationToken);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _store?.Dispose();
            _store = null;
        }
    }

    private FileHomeostatStore Store()
    {
        if (Volatile.Read(ref _store) is { } open)
        {
            return open;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_store is not null)
            {
                return _store;
            }

            var now = Environment.TickCount64;
            if (_failure is not null && now < _nextAttempt)
            {
                throw new IOException($"The Homeostat store file '{_path}' cannot be opened yet.", _failure);
            }

            try
            {
                _store = new FileHomeostatStore(_path, _options, _logger);
                if (_failure is not null)
                {
                    _logger.LogWarning("The Homeostat store file {Path} is open again.", _path);
                }

                _failure = null;
                return _store;
            }
            catch (Exception ex)
            {
                if (_failure is null)
                {
                    _logger.LogCritical(ex, "The Homeostat store file {Path} cannot be opened: Homeostat does not act until it can. Trying again every {Retry}.", _path, RetryEvery);
                }

                _failure = ex;
                _nextAttempt = now + (long)RetryEvery.TotalMilliseconds;
                throw;
            }
        }
    }
}
