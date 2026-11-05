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

    // Guards replacing the view: never held across a store call, so a change made here applies at once.
    private readonly object _viewGate = new();
    private long _reads;
    private readonly InFlight<bool> _writes = new();
    private readonly object _readBackGate = new();
    private Task? _readBack;   // guarded by _readBackGate
    private bool _readBackAgain;   // guarded by _readBackGate

    private async Task ReadBackLoopAsync()
    {
        while (true)
        {
            try
            {
                await TryRefreshAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reading back the Homeostat control state failed.");
            }

            lock (_readBackGate)
            {
                if (!_readBackAgain)
                {
                    _readBack = null;   // a change made from now on starts a new read-back
                    return;
                }

                _readBackAgain = false;
            }
        }
    }
    private Task<ControlState>? _pendingRead;   // guarded by _refreshing
    private long _pendingReadNumber;
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
            long read;
            try
            {
                // One read at a time: while a read abandoned at its timeout still runs (a store blocked on I/O), the
                // refresh waits for it instead of starting another every few seconds, which would pile up threads.
                if (_pendingRead is not { IsCompleted: false })
                {
                    // Changes written after this point may be missing from what the read returns: they stay applied on top.
                    _pendingReadNumber = Interlocked.Increment(ref _reads);
                    _pendingRead = Task.Run(() => _store.GetControlStateAsync(CancellationToken.None).AsTask(), CancellationToken.None);
                }

                read = _pendingReadNumber;
                next = Validate(await _pendingRead.WaitAsync(ReadTimeout, cancellationToken).ConfigureAwait(false));
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

                // The writes may work even when the read does not (an unreadable record): try the pending ones anyway.
                await WriteDeferredAsync(Current, read: 0).ConfigureAwait(false);
                return false;
            }

            if (_readFailing)
            {
                _readFailing = false;
                _logger.LogInformation("The Homeostat control state can be read from the store again.");
            }

            await WriteDeferredAsync(next, read).ConfigureAwait(false);
            lock (_viewGate)
            {
                Replace(ApplyPending(next, read));
            }

            return true;
        }
        finally
        {
            _refreshing.Release();
            Dispatch();
        }
    }

    /// <summary>
    /// Applies a change in the safe direction (a pause, a switch turned off, a release): at once to this instance,
    /// whatever the store does, then written to the store; a change the store does not take in time is written again at
    /// every refresh until it does, unless the store shows a newer decision first (<paramref name="superseded"/>).
    /// </summary>
    /// <remarks>
    /// It takes no cancellation token: a caller that gives up (a request aborted while the store is slow) must not lose a
    /// change that makes Homeostat do less. The wait is bounded by <see cref="WriteTimeout"/>.
    /// </remarks>
    /// <returns><c>true</c> if the store took the change now; <c>false</c> if it is deferred.</returns>
    public async Task<bool> ChangeOrDeferAsync(
        string key,
        Func<IHomeostatStore, CancellationToken, ValueTask<bool>> change,
        Func<ControlState, ControlState> apply,
        Func<ControlState, bool> superseded)
    {
        var deferred = new Deferred(change, apply, superseded);
        lock (_viewGate)
        {
            // Registered before the view changes, so a refresh that read the store earlier applies it on top too.
            _deferred[key] = deferred;
            if (_view is not null)
            {
                Replace(apply(Current));
            }
        }

        Dispatch();
        if (!await TryWriteAsync(key, deferred).ConfigureAwait(false))
        {
            _logger.LogWarning("The Homeostat store did not take a change ({Change}): it applies to this instance now and is retried at every refresh.", key);
            return false;
        }

        await ReadBackAsync().ConfigureAwait(false);
        return true;
    }

    // Writes one change; on success, records which reads may still miss it.
    private async Task<bool> TryWriteAsync(string key, Deferred deferred)
    {
        bool applied;
        try
        {
            // One write at a time per change, and per thing changed: while a write abandoned at its timeout still runs
            // (a store blocked on I/O), retries wait for it instead of starting another, also when a newer change of the
            // same thing (a release asked again) replaced the change it was writing.
            var write = deferred.Writing;
            if (write is null || write.IsCompleted)
            {
                if (_writes.IsRunning(key))
                {
                    return false;   // still pending: tried again at the next refresh
                }

                write = _writes.Start(key, () => deferred.Change(_store, CancellationToken.None).AsTask());
                deferred.Writing = write;
            }

            applied = await write.WaitAsync(WriteTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Writing a Homeostat change ({Change}) failed.", key);
            return false;
        }

        if (!applied)
        {
            // Refused: the store holds a later decision about the same thing (taken elsewhere meanwhile), or already
            // has this change (written twice, by the caller and by a refresh). Either way the store is right: kept as
            // written until a read that starts from now on shows what the store has, so an older read cannot make the
            // view flap.
            _logger.LogDebug("A Homeostat change ({Change}) was not written again: the store already decided.", key);
        }

        // A read that starts from now on sees it; earlier ones may not.
        deferred.WrittenBefore = Volatile.Read(ref _reads) + 1;
        return true;
    }

    // Under _refreshing, after a successful read: writes what is still waiting (in parallel, so a slow store costs one
    // write timeout per refresh, not one per change), and drops what a newer decision replaced.
    private async Task WriteDeferredAsync(ControlState state, long read)
    {
        var writes = new List<Task>();
        foreach (var (key, deferred) in _deferred)
        {
            if (deferred.Superseded(state) || deferred.WrittenBefore is { } before && read >= before)
            {
                // Replaced by a newer decision, or written and visible in this read.
                _deferred.TryRemove(new KeyValuePair<string, Deferred>(key, deferred));
                continue;
            }

            if (deferred.WrittenBefore is null && !deferred.Withdrawn)
            {
                writes.Add(WriteOneAsync(key, deferred));
            }
        }

        await Task.WhenAll(writes).ConfigureAwait(false);

        async Task WriteOneAsync(string key, Deferred deferred)
        {
            if (await TryWriteAsync(key, deferred).ConfigureAwait(false))
            {
                _logger.LogInformation("A deferred Homeostat change ({Change}) was written to the store.", key);
            }
        }
    }

    // Under _viewGate: what a read may not show yet (deferred, or written after the read started) applies on top.
    private ControlState ApplyPending(ControlState state, long read)
    {
        foreach (var deferred in _deferred.Values)
        {
            if (deferred.WrittenBefore is not { } before || read < before)
            {
                state = deferred.Apply(state);
            }
        }

        return state;
    }

    private sealed class Deferred(
        Func<IHomeostatStore, CancellationToken, ValueTask<bool>> change,
        Func<ControlState, ControlState> apply,
        Func<ControlState, bool> superseded)
    {
        /// <summary>Writes the change; <c>false</c> when the store already holds a later decision.</summary>
        public Func<IHomeostatStore, CancellationToken, ValueTask<bool>> Change { get; } = change;

        public Func<ControlState, ControlState> Apply { get; } = apply;

        public Func<ControlState, bool> Superseded { get; } = superseded;

        private long _writtenBefore;   // 0 until written; read and written across threads

        /// <summary>Set once written: reads numbered from this value on include it.</summary>
        public long? WrittenBefore
        {
            get => Volatile.Read(ref _writtenBefore) is var value and > 0 ? value : null;
            set => Volatile.Write(ref _writtenBefore, value ?? 0);
        }

        /// <summary>A newer decision about the same thing is being written: not written meanwhile.</summary>
        public volatile bool Withdrawn;

        /// <summary>The latest write of this change, which may still run after its timeout.</summary>
        public Task<bool>? Writing { get; set; }
    }

    /// <summary>
    /// Applies a change that lets Homeostat act (a resume, a switch turned on, a claim) to the store; store failures reach
    /// the caller. Once the store took it, <paramref name="apply"/> updates this instance's view at once, and keeps
    /// applying on top of reads that may have started before the write, so the view never flaps back.
    /// </summary>
    /// <param name="key">What the change is about ("pause", "switch:{name}"): an older pending change of the same thing is
    /// held back while this one is written, and dropped once the store takes this one.</param>
    /// <param name="change">Writes the change; <c>false</c> when the store refused it (a later decision, or another
    /// owner): then nothing changes here, and an older pending change stands.</param>
    /// <param name="apply">The same change, applied to this instance's view.</param>
    /// <param name="cancellationToken">Cancels the wait for the write.</param>
    /// <returns>Whether the store took the change.</returns>
    public async Task<bool> ChangeAsync(
        string key,
        Func<IHomeostatStore, CancellationToken, ValueTask<bool>> change,
        Func<ControlState, ControlState> apply,
        CancellationToken cancellationToken)
    {
        // A newer decision about the same thing: an older pending change must not be written after it, so it is held
        // back while this write runs. If this write fails or is refused, the older change still stands (a resume that
        // did not reach the store must not end a pause that is waiting to be written).
        _deferred.TryGetValue(key, out var older);
        if (older is not null)
        {
            older.Withdrawn = true;
        }

        bool applied;
        try
        {
            applied = await Bounded(() => change(_store, cancellationToken), WriteTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (older is not null)
            {
                older.Withdrawn = false;
            }

            throw;
        }

        if (!applied)
        {
            if (older is not null)
            {
                older.Withdrawn = false;
            }

            return false;
        }

        // Written: shown here at once, and on top of any read that started before the write.
        var written = new Deferred((_, _) => ValueTask.FromResult(true), apply, static _ => false) { WrittenBefore = Volatile.Read(ref _reads) + 1 };
        lock (_viewGate)
        {
            // A change in the safe direction made while this write ran (a pause during a resume) is the later decision:
            // it stays pending and shown, and reaches the store after this one. Otherwise this change is shown.
            _deferred.TryGetValue(key, out var pending);
            // (A change already written, by a concurrent call of this method, is not newer: this one is shown.)
            if (pending is null || ReferenceEquals(pending, older) || pending.WrittenBefore is not null)
            {
                if (older is not null)
                {
                    _deferred.TryRemove(new KeyValuePair<string, Deferred>(key, older));
                }

                _deferred[key] = written;

                // Never read yet: the rest of the state (a pause above all) is still unknown, so it stays unknown.
                if (_view is not null)
                {
                    Replace(apply(Current));
                }
            }
        }

        Dispatch();
        await ReadBackAsync().ConfigureAwait(false);
        return true;
    }

    // The view already shows the change: the caller does not wait behind a refresh (which may be slow) to read it back.
    // Unless the state was never read (before the refresher's first read, or without a host): then the change cannot be
    // shown until it is, so the caller waits for that read, bounded.
    private async Task ReadBackAsync()
    {
        // Coalesced: one read-back at a time, and at most one more asked for meanwhile, however many changes are made, so
        // a burst of changes on a slow store does not queue reads ahead of the periodic refresh.
        Task reading;
        lock (_readBackGate)
        {
            if (_readBack is { } running)
            {
                _readBackAgain = true;
                reading = running;
            }
            else
            {
                reading = _readBack = Task.Run(ReadBackLoopAsync, CancellationToken.None);
            }
        }

        if (_view is null)
        {
            try
            {
                await reading.WaitAsync(ReadTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Still unknown: it applies once the state is read.
            }
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

    // Under _viewGate: the queue keeps the changes in the order they happened.
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
    SharedControl control, IOptions<HomeostatOptions> options, TimeProvider timeProvider, ILogger<SharedControlRefresher> logger,
    HomeostatSwitches? switches = null) : BackgroundService
{
    internal static readonly TimeSpan StartupWait = TimeSpan.FromSeconds(5);

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
            catch (Exception ex)
            {
                // Never out of a background service (it would stop the application): the next refresh tries again.
                logger.LogError(ex, "Refreshing the Homeostat control state failed.");
            }
        }
    }
}
