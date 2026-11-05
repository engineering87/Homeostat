using Homeostat.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homeostat.Conditions;

/// <summary>
/// Keeps the baselines of seasonal rules across restarts: loads them from the store before the engine processes
/// signals, saves the changed ones every few minutes and when the application stops. Without it, a weekly baseline
/// would start over at every deployment.
/// </summary>
internal sealed class SeasonalBaselineKeeper : IHostedService, IAsyncDisposable
{
    internal static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);

    /// <summary>Longest wait for the store at startup (or at shutdown): an unreachable store must not hold the application.</summary>
    internal static readonly TimeSpan StoreWait = TimeSpan.FromSeconds(10);

    private readonly ConditionSet _conditions;
    private readonly IHomeostatStore _store;
    private bool _warnedNoDocuments;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SeasonalBaselineKeeper> _logger;
    private readonly Dictionary<SeasonalBaseline, long> _saved = [];
    private readonly SemaphoreSlim _sync = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _stopping;
    private Task _loop = Task.CompletedTask;

    public SeasonalBaselineKeeper(ConditionSet conditions, IHomeostatStore store, TimeProvider timeProvider, ILogger<SeasonalBaselineKeeper> logger)
    {
        _conditions = conditions;
        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Completes once the saved baselines are loaded (or could not be within <see cref="StoreWait"/>).</summary>
    public Task Ready => _ready.Task;

    private List<SeasonalBaseline> Baselines => [.. _conditions.Trackers.SelectMany(t => t.Definition.Baselines)];

    /// <summary>For a replay, which learns from the replayed signals only: nothing to load.</summary>
    public void SkipLoading() => _ready.TrySetResult();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Baselines.Count > 0)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(StoreWait);

                // Bounded even when the store ignores the token: the application's startup must not wait for it.
                await Task.Run(() => SyncAsync(limit.Token), CancellationToken.None).WaitAsync(limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("The saved seasonal baselines could not be loaded within {Wait}: learning starts over.", StoreWait);
        }
        finally
        {
            // The engine waits for this, so it never compares signals with a baseline that is still loading.
            _ready.TrySetResult();
        }

        // Always: a seasonal rule added later by a configuration reload is loaded and saved by the loop.
        _stopping = new CancellationTokenSource();
        var token = _stopping.Token;
        _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is { } stopping)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop was just canceled, or the host stopped waiting.
            }
        }

        // The final save gets its own short deadline (the shutdown token may already be canceled), and is bounded even
        // when the store ignores its token or blocks: the application's shutdown never waits for it longer.
        using var limit = new CancellationTokenSource(StoreWait);
        try
        {
            await Task.Run(() => SyncAsync(limit.Token), CancellationToken.None).WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            _logger.LogWarning("The seasonal baselines could not be saved within {Wait} at shutdown.", StoreWait);
        }
    }

    public ValueTask DisposeAsync()
    {
        // The semaphore is not disposed: a sync abandoned at shutdown may still release it later.
        _stopping?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Loads the baselines not loaded yet and saves the changed ones, in parallel, including the baselines that a
    /// configuration reload has just replaced (so their last minutes of learning are kept).
    /// </summary>
    internal async Task SyncAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var current = Baselines;
            if (current.Count > 0 && !_store.SupportsDocuments && !_warnedNoDocuments)
            {
                _warnedNoDocuments = true;
                _logger.LogWarning("The Homeostat store does not keep documents: seasonal baselines are learned again after every restart.");
            }

            var replaced = _saved.Keys.Except(current).ToList();
            await Task.WhenAll(current.Concat(replaced).Select(b => SyncOneAsync(b, load: current.Contains(b), cancellationToken))).ConfigureAwait(false);
            foreach (var baseline in replaced)
            {
                _saved.Remove(baseline);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    private async Task SyncOneAsync(SeasonalBaseline baseline, bool load, CancellationToken cancellationToken)
    {
        try
        {
            if (load && !baseline.Loaded)
            {
                if (await _store.GetDocumentAsync(DocumentKey(baseline), cancellationToken).ConfigureAwait(false) is { } document
                    && !baseline.Import(document))
                {
                    _logger.LogInformation("The saved baseline {Key} was learned with another cycle or version; learning starts over.", baseline.Key);
                }

                baseline.Loaded = true;
            }

            long saved;
            lock (_saved)
            {
                saved = _saved.GetValueOrDefault(baseline, -1);
            }

            var changes = baseline.Changes;
            if (baseline.Loaded && changes > 0 && changes != saved)
            {
                await _store.SetDocumentAsync(DocumentKey(baseline), baseline.Export(), cancellationToken).ConfigureAwait(false);
                lock (_saved)
                {
                    _saved[baseline] = changes;
                }
            }
        }
        catch (Exception ex)
        {
            // Includes timeouts of the store (TaskCanceledException from an HTTP client, or our own deadline).
            _logger.LogWarning(ex, "Loading or saving the seasonal baseline {Key} failed; retried in {Interval}.", baseline.Key, SaveInterval);
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(SaveInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await SyncAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopping.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The seasonal baseline keeper stopped; baselines are saved again when the application stops.");
        }
    }

    private static string DocumentKey(SeasonalBaseline baseline) => $"baseline:{baseline.Key}";

    /// <summary>Syncs now, without waiting for the timer: after a configuration reload.</summary>
    internal void SyncSoon()
    {
        var token = _stopping?.Token ?? CancellationToken.None;
        _ = Task.Run(() => SyncAsync(token), CancellationToken.None);
    }
}
