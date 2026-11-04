using System.Collections.Concurrent;
using Homeostat.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Engine;

/// <summary>
/// This instance's view of the control state kept in the store (pause, application switches, approval requests):
/// read from the store at startup and every <see cref="HomeostatOptions.ControlRefreshInterval"/>, and right after
/// every change made here. Reads (<c>IsPaused</c>, <c>IsOn</c>) use the view and never wait for the store.
/// </summary>
internal sealed class SharedControl
{
    private static readonly View EmptyView = new(ControlState.Empty);

    // A store that never answers must not hold the refresh, and with it every pause and switch change, forever.
    internal static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    // The same for a write: a change that is not written in time is deferred (when it is safe) or fails.
    internal static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    // Changes in the safe direction (a pause, a switch turned off) that the store could not take: they apply to this
    // instance at once, are written at every refresh until the store takes them, and are dropped when the store shows
    // a newer decision. Keyed by what they change ("pause", "switch:{name}"): the latest intent wins.
    private readonly ConcurrentDictionary<string, Deferred> _deferred = new(StringComparer.Ordinal);

    private readonly IHomeostatStore _store;
    private readonly ILogger<SharedControl> _logger;
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly ConcurrentQueue<(ControlState Previous, ControlState Next)> _changes = new();
    private int _dispatching;
    private bool _readFailing;

    // Null until the state has been read once: until then nobody knows whether Homeostat is paused.
    private volatile View? _view;

    public SharedControl(IHomeostatStore store, ILogger<SharedControl> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Raised after every refresh, with the previous and the new state, in the order of the changes and outside the
    /// refresh, so a handler may change the control state itself.
    /// </summary>
    public event Action<ControlState, ControlState>? Refreshed;

    /// <summary>Whether the state has been read from the store at least once.</summary>
    public bool IsKnown => _view is not null;

    public ControlState Current => (_view ?? EmptyView).State;

    public bool IsSwitchOn(string name) => (_view ?? EmptyView).Switches.Contains(name);

    public IHomeostatStore Store => _store;

    /// <summary>Reads the state from the store; an unreachable store keeps the last known state.</summary>
    public async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ControlState next;
            try
            {
                next = Validate(await Bounded(() => _store.GetControlStateAsync(cancellationToken), ReadTimeout, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Once when it starts failing, not at every refresh: an outage must not flood the log.
                if (!_readFailing)
                {
                    _readFailing = true;
                    _logger.LogWarning(ex, "Could not read the Homeostat control state from the store; keeping the last known state until it answers.");
                }
                else
                {
                    _logger.LogDebug(ex, "The Homeostat control state still cannot be read.");
                }

                return false;
            }

            if (_readFailing)
            {
                _readFailing = false;
                _logger.LogInformation("The Homeostat control state can be read from the store again.");
            }

            next = await WriteDeferredAsync(next, cancellationToken).ConfigureAwait(false);
            Replace(next);
            return true;
        }
        finally
        {
            _refreshing.Release();
            Dispatch();
        }
    }

    /// <summary>
    /// Applies a change in the safe direction (a pause, a switch turned off, a release): written to the store when it
    /// answers in time; otherwise applied to this instance at once and written at every refresh until the store takes
    /// it, unless the store shows a newer decision first (<paramref name="superseded"/>).
    /// </summary>
    /// <returns><c>true</c> if the store took the change now; <c>false</c> if it is deferred.</returns>
    public async Task<bool> ChangeOrDeferAsync(
        string key,
        Func<IHomeostatStore, CancellationToken, ValueTask> change,
        Func<ControlState, ControlState> apply,
        Func<ControlState, bool> superseded,
        CancellationToken cancellationToken)
    {
        _deferred.TryRemove(key, out _);
        try
        {
            await Bounded(() => change(_store, cancellationToken), WriteTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The Homeostat store did not take a change ({Change}): it applies to this instance now and is retried at every refresh.", key);
            _deferred[key] = new Deferred(change, apply, superseded);
            await ApplyLocallyAsync(apply).ConfigureAwait(false);
            return false;
        }

        try
        {
            if (await TryRefreshAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            // Applied locally below.
        }

        await ApplyLocallyAsync(apply).ConfigureAwait(false);
        return true;
    }

    /// <summary>Forgets a deferred change: a newer decision replaces it (a switch turned on again, a resume).</summary>
    public void Forget(string key) => _deferred.TryRemove(key, out _);

    /// <summary>Whether changes wait to be written to the store.</summary>
    public bool HasDeferredChanges => !_deferred.IsEmpty;

    private async Task ApplyLocallyAsync(Func<ControlState, ControlState> apply)
    {
        await _refreshing.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // Never read yet: nothing to apply to (and an unknown state already counts as paused, with no switch on).
            if (_view is not null)
            {
                Replace(apply(Current));
            }
        }
        finally
        {
            _refreshing.Release();
            Dispatch();
        }
    }

    // Under _refreshing, after a successful read: write what was deferred, and keep applying what is still waiting.
    private async Task<ControlState> WriteDeferredAsync(ControlState state, CancellationToken cancellationToken)
    {
        foreach (var (key, deferred) in _deferred)
        {
            if (deferred.Superseded(state))
            {
                _deferred.TryRemove(new KeyValuePair<string, Deferred>(key, deferred));
                continue;
            }

            try
            {
                await Bounded(() => deferred.Change(_store, cancellationToken), WriteTimeout, cancellationToken).ConfigureAwait(false);
                _deferred.TryRemove(new KeyValuePair<string, Deferred>(key, deferred));
                _logger.LogInformation("A deferred Homeostat change ({Change}) was written to the store.", key);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Still waiting: tried again at the next refresh.
            }

            // Read before the write: apply it on top either way.
            state = deferred.Apply(state);
        }

        return state;
    }

    private sealed record Deferred(
        Func<IHomeostatStore, CancellationToken, ValueTask> Change,
        Func<ControlState, ControlState> Apply,
        Func<ControlState, bool> Superseded);

    /// <summary>
    /// Applies a change to the store, then refreshes the view. Store failures reach the caller. When the change was
    /// written but the state cannot be read back, <paramref name="apply"/> updates the view here, so a change made on
    /// this instance applies to it at once all the same.
    /// </summary>
    public async Task ChangeAsync(
        Func<IHomeostatStore, CancellationToken, ValueTask> change,
        Func<ControlState, ControlState>? apply,
        CancellationToken cancellationToken,
        string? key = null)
    {
        await Bounded(() => change(_store, cancellationToken), WriteTimeout, cancellationToken).ConfigureAwait(false);

        // Written: a deferred change of the same thing is older, and must not be written after it.
        if (key is not null)
        {
            _deferred.TryRemove(key, out _);
        }

        bool refreshed;
        try
        {
            refreshed = await TryRefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            refreshed = false;
        }

        if (refreshed || apply is null)
        {
            return;
        }

        await _refreshing.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // Never read yet: the rest of the state (a pause above all) is still unknown, so it stays unknown.
            if (_view is not null)
            {
                Replace(apply(Current));
            }
        }
        finally
        {
            _refreshing.Release();
            Dispatch();
        }
    }

    /// <summary>
    /// A store call that Homeostat waits for at most <paramref name="timeout"/>, even when the store blocks before
    /// returning its task (synchronous I/O, a lock): the call starts on the thread pool, never on the caller's thread.
    /// </summary>
    internal static Task<T> Bounded<T>(Func<ValueTask<T>> call, TimeSpan timeout, CancellationToken cancellationToken)
        => Task.Run(() => call().AsTask(), CancellationToken.None).WaitAsync(timeout, cancellationToken);

    /// <inheritdoc cref="Bounded{T}(Func{ValueTask{T}}, TimeSpan, CancellationToken)"/>
    internal static Task Bounded(Func<ValueTask> call, TimeSpan timeout, CancellationToken cancellationToken)
        => Task.Run(() => call().AsTask(), CancellationToken.None).WaitAsync(timeout, cancellationToken);

    /// <summary>A copy of the state with another pause.</summary>
    public static ControlState WithPause(ControlState state, PauseState? pause)
        => new() { Pause = pause, Switches = state.Switches, Approvals = state.Approvals };

    /// <summary>A copy of the state with a switch turned on (replacing its previous state) or, with <c>null</c>, off.</summary>
    public static ControlState WithSwitch(ControlState state, string name, SwitchState? value)
    {
        var switches = state.Switches.Where(s => !string.Equals(s.Name, name, StringComparison.Ordinal));
        if (value is not null)
        {
            switches = switches.Append(value);
        }

        return new() { Pause = state.Pause, Switches = [.. switches.OrderBy(s => s.Name, StringComparer.Ordinal)], Approvals = state.Approvals };
    }

    private static ControlState Validate(ControlState? state)
    {
        // A custom store returning an incomplete state is treated like a failed read, not trusted.
        if (state?.Switches is null || state.Approvals is null
            || state.Switches.Any(s => s?.Name is null) || state.Approvals.Any(a => a?.Request is null))
        {
            throw new InvalidDataException("The store returned an incomplete control state.");
        }

        return state;
    }

    // Under _refreshing: the queue keeps the changes in the order they happened.
    private void Replace(ControlState next)
    {
        var previous = Current;
        _view = new View(next);
        _changes.Enqueue((previous, next));
    }

    // One thread at a time raises the queued changes; a handler that changes the state queues its own change, which
    // the same thread raises next, instead of waiting for itself.
    private void Dispatch()
    {
        while (!_changes.IsEmpty && Interlocked.CompareExchange(ref _dispatching, 1, 0) == 0)
        {
            try
            {
                while (_changes.TryDequeue(out var change))
                {
                    try
                    {
                        Refreshed?.Invoke(change.Previous, change.Next);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Applying the Homeostat control state failed.");
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _dispatching, 0);
            }
        }
    }

    private sealed class View(ControlState state)
    {
        public ControlState State { get; } = state;

        public HashSet<string> Switches { get; } = state.Switches.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
    }
}

/// <summary>Reads the control state at startup and then periodically, so changes made on other instances apply here.</summary>
internal sealed class SharedControlRefresher(
    SharedControl control, IOptions<HomeostatOptions> options, TimeProvider timeProvider, HomeostatSwitches? switches = null) : BackgroundService
{
    private static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(5);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // The guardrails are fixed from now on (see HomeostatOptions.Freeze).
        options.Value.Freeze();

        // A pause set before this instance started applies from its first signal. Until the state is read, Homeostat
        // counts as paused (see HomeostatControl.PauseReason), so a store unreachable at startup delays healing
        // rather than letting it run through a pause it cannot see.
        // The application's startup waits a few seconds at most: past that the refresh goes on in the background.
        try
        {
            await Task.Run(() => control.TryRefreshAsync(cancellationToken), CancellationToken.None).WaitAsync(StartupWait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Still paused (state unknown) until the read completes.
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // The application switches this process holds are released by the other instances as soon as their condition
        // is not active there, instead of after the renewal lapses.
        if (switches is not null)
        {
            try
            {
                await switches.AbandonOwnershipAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: the renewal lapses anyway.
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.ControlRefreshInterval;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Until the state is first read, retry sooner: healing waits for it.
            var delay = control.IsKnown ? interval : TimeSpan.FromSeconds(Math.Min(1, interval.TotalSeconds));
            try
            {
                await Task.Delay(delay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (await control.TryRefreshAsync(stoppingToken).ConfigureAwait(false) && switches is not null)
                {
                    await switches.MaintainOwnershipAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
