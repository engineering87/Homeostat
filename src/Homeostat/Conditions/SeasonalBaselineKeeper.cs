using Homeostat.Engine;
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

    /// <summary>Longest wait for the saved baselines before the engine starts without them, and for the store at shutdown.</summary>
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
    private readonly InFlight<string?> _calls = new();

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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Loaded in the background: an unreachable store must not hold the application's startup. The engine waits for
        // Ready instead, so it never compares signals with a baseline that is still loading (signals queue meanwhile).
        // Always started: a seasonal rule added later by a configuration reload is loaded and saved by the loop.
        _stopping = new CancellationTokenSource();
        var token = _stopping.Token;
        _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            if (Baselines.Count > 0)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(StoreWait);

                // Bounded even when the store ignores the token.
                await Task.Run(() => SyncAsync(limit.Token), CancellationToken.None).WaitAsync(limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            _logger.LogWarning("The saved seasonal baselines could not be loaded within {Wait}: learning starts over.", StoreWait);
        }
        catch (OperationCanceledException)
        {
            // Stopping before the baselines were loaded.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The saved seasonal baselines could not be loaded: learning starts over.");
        }
        finally
        {
            _ready.TrySetResult();
        }

        await LoopAsync(token).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is { } stopping)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            try
            {
                // Every store call of the loop is bounded and follows the token, so it ends at once; the wait is bounded
                // anyway, so the application's shutdown never waits for the store.
                await _loop.WaitAsync(StoreWait, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Expected: the loop was just canceled, or Homeostat (or the host) stopped waiting.
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
                var key = DocumentKey(baseline);
                if (await BoundedAsync(key, () => _store.GetDocumentAsync(key, cancellationToken).AsTask(), SharedControl.ReadTimeout, cancellationToken).ConfigureAwait(false) is string document
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
                var key = DocumentKey(baseline);
                var document = baseline.Export();
                await BoundedAsync(key, async () =>
                {
                    await _store.SetDocumentAsync(key, document, cancellationToken).ConfigureAwait(false);
                    return null;
                }, SharedControl.WriteTimeout, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// One store call for a document, bounded even when the store ignores its token or blocks its caller. A call
    /// abandoned at its timeout is not started again until it ends: the periodic save would otherwise pile up a blocked
    /// thread every few minutes.
    /// </summary>
    private async Task<string?> BoundedAsync(string key, Func<Task<string?>> call, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_calls.IsRunning(key))
        {
            throw new TimeoutException($"The previous store call for {key}, abandoned at its timeout, is still running.");
        }

        return await _calls.Start(key, call).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Syncs now, without waiting for the timer: after a configuration reload.</summary>
    internal void SyncSoon()
    {
        var token = _stopping?.Token ?? CancellationToken.None;
        _ = Task.Run(() => SyncAsync(token), CancellationToken.None);
    }
}
