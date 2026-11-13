using System.Collections.Concurrent;
using Homeostat.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Engine;

/// <summary>
/// This instance's view of the control state kept in the store (pause, pauses of groups, application switches, approval
/// requests):
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
    // a newer decision. Keyed by what they change ("pause", "group-pause:{group}", "switch:{name}"): the latest intent
    // wins.
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

    /// <summary>
    /// What this instance knows of the state: the view or, before the state is first read, the changes made here that
    /// are still pending, applied to an empty state. Limits are checked against it (decision G4): before the first read
    /// the view is empty, but every pending change is an entry and a write of its own.
    /// </summary>
    public ControlState Known
    {
        get
        {
            lock (_viewGate)
            {
                return KnownLocked();
            }
        }
    }

    // Under _viewGate.
    private ControlState KnownLocked() => _view?.State ?? ApplyPending(ControlState.Empty, read: 0);

    public bool IsSwitchOn(string name) => (_view ?? EmptyView).Switches.Contains(name);

    /// <summary>The pause of a group in this instance's view, or <c>null</c> (also before the state is first read).</summary>
    public GroupPauseState? GroupPause(string group) => (_view ?? EmptyView).GroupPauses.GetValueOrDefault(group);

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
    /// <param name="key">What the change is about ("pause", "switch:{name}").</param>
    /// <param name="decidedAt">When the change was decided: the store orders decisions by this time.</param>
    /// <param name="change">Writes the change; <c>false</c> when the store holds a later decision.</param>
    /// <param name="apply">The same change, applied to this instance's view.</param>
    /// <param name="superseded">Whether a state read from the store shows a newer decision.</param>
    /// <param name="later">
    /// When the store refuses the change: describes the later decision the store shows, or returns <c>null</c> when the
    /// store already shows the safe state (nothing to warn about).
    /// </param>
    /// <param name="limit">
    /// A limit of the store (the number of groups paused at once): says why a state does not admit the change, or returns
    /// <c>null</c>. Checked against what this instance knows (<see cref="Known"/>) before the change applies; and when the
    /// store's write fails with <see cref="InvalidOperationException"/>, a state read from the store that does not admit
    /// the change tells the store's refusal from an outage. A refused change is dropped and no longer applies here.
    /// </param>
    /// <returns>
    /// <c>true</c> if the store answered now (it took the change, or refused it for a later decision, which is logged
    /// as a warning); <c>false</c> if it is deferred.
    /// </returns>
    /// <exception cref="InvalidOperationException">The change is over the <paramref name="limit"/>, here or in the store.</exception>
    public async Task<bool> ChangeOrDeferAsync(
        string key,
        DateTimeOffset decidedAt,
        Func<IHomeostatStore, CancellationToken, ValueTask<bool>> change,
        Func<ControlState, ControlState> apply,
        Func<ControlState, bool> superseded,
        Func<ControlState, string?>? later = null,
        Func<ControlState, string?>? limit = null)
    {
        var deferred = new Deferred(change, apply, superseded) { DecidedAt = decidedAt, IsSafe = true, Later = later, Limit = limit };
        lock (_viewGate)
        {
            // Checked under the gate with the registration: concurrent changes cannot pass the limit together.
            if (limit?.Invoke(KnownLocked()) is { } refused)
            {
                throw new InvalidOperationException(refused);
            }

            // Registered before the view changes, so a refresh that read the store earlier applies it on top too.
            _deferred[key] = deferred;
            if (_view is not null)
            {
                Replace(apply(Current));
            }
        }

        Dispatch();
        var written = await TryWriteAsync(key, deferred).ConfigureAwait(false);
        if (written == Write.OverLimit)
        {
            if (await ReadOverLimitAsync(key, deferred).ConfigureAwait(false) is { } refused)
            {
                Drop(key, deferred, refused.State, refused.Read);
                throw new InvalidOperationException(refused.Reason, deferred.LimitError);
            }

            written = Write.Pending;   // not confirmed: an outage, retried at every refresh
        }

        if (written == Write.Pending)
        {
            _logger.LogWarning("The Homeostat store did not take a change ({Change}): it applies to this instance now and is retried at every refresh.", Safe(key));
            return false;
        }

        if (deferred.Refused)
        {
            // The caller waits for the store anyway: the warning is logged before it returns.
            await ReportRefusedAsync(key, deferred).ConfigureAwait(false);
        }

        await ReadBackAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// A change in the safe direction that the store refused because it holds a later decision (decision order, S2: a
    /// resume or a turn-on decided after it, possibly on an instance whose clock is ahead). The store is right, but the
    /// person who paused or turned off must learn that their change does not stand: logged as a warning naming what
    /// the store holds. Bounded, never throws.
    /// </summary>
    private async Task ReportRefusedAsync(string key, Deferred deferred)
    {
        if (Volatile.Read(ref deferred.Reported) == 1)
        {
            return;
        }

        ControlState? state;
        try
        {
            state = Validate(await Bounded(() => _store.GetControlStateAsync(CancellationToken.None), ReadTimeout, CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read what the Homeostat store holds instead of a refused change ({Change}).", Safe(key));
            state = null;
        }

        ReportRefused(key, deferred, state);
    }

    // With the state the store holds now (null when it could not be read).
    private void ReportRefused(string key, Deferred deferred, ControlState? state)
    {
        if (Interlocked.Exchange(ref deferred.Reported, 1) == 1)
        {
            return;
        }

        var later = state is null
            ? "a later decision about the same thing (the store could not be read to name it)"
            : deferred.Later is { } describe ? describe(state) : "a later decision about the same thing";
        if (later is null)
        {
            // The store already shows the safe state (released or paused meanwhile): nothing is lost.
            _logger.LogDebug("A Homeostat change ({Change}) was not written: the store already shows it.", Safe(key));
            return;
        }

        _logger.LogWarning(
            "The Homeostat store refused a change ({Change}) decided at {DecidedAt:O}: it holds a later decision ({Later}). "
            + "Decisions are ordered by the clocks of the instances that took them; the change no longer applies once this instance reads the store again.",
            Safe(key), deferred.DecidedAt, later.ReplaceLineEndings(" "));
    }

    // A switch name may come from an HTTP request: a line break must not start a fake log entry.
    private static string Safe(string value) => value.ReplaceLineEndings(" ");

    private enum Write
    {
        /// <summary>Not written (the store failed or did not answer in time): still pending.</summary>
        Pending,

        /// <summary>The store answered: it took the change, or refused it for a later decision (<see cref="Deferred.Refused"/>).</summary>
        Answered,

        /// <summary>
        /// The store failed with <see cref="InvalidOperationException"/>, as it does for a change over its limit, but also
        /// as some stores and clients do in an outage: still pending until a read of the store confirms the limit.
        /// </summary>
        OverLimit,
    }

    /// <summary>
    /// After a write failed with <see cref="InvalidOperationException"/>: reads the store (bounded) and returns why it
    /// refuses the change, with the state read and its number, or <c>null</c> when the state it holds admits the change or
    /// cannot be read (then it was an outage, or room was made meanwhile: the change stays pending, fail closed).
    /// </summary>
    private async Task<(string Reason, ControlState State, long Read)?> ReadOverLimitAsync(string key, Deferred deferred)
    {
        try
        {
            // Numbered like a refresh's read: changes written after this point stay applied on top of it.
            var read = Interlocked.Increment(ref _reads);
            var state = Validate(await Bounded(() => _store.GetControlStateAsync(CancellationToken.None), ReadTimeout, CancellationToken.None).ConfigureAwait(false));
            return deferred.Limit?.Invoke(state) is { } reason ? (reason, state, read) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the Homeostat store to check a failed change ({Change}) against its limit.", Safe(key));
            return null;
        }
    }

    /// <summary>
    /// Drops a change the store refused for its limit: it no longer applies here (the store will never hold it, and
    /// retrying it at every refresh would keep, on this instance alone, a change the cluster does not have). With the
    /// state just read, the view is rebuilt from it without the change; a refresh rebuilds it itself.
    /// </summary>
    /// <returns>Whether this call dropped it (not a concurrent one, nor a newer change of the same thing).</returns>
    private bool Drop(string key, Deferred deferred, ControlState? state, long read)
    {
        bool dropped;
        lock (_viewGate)
        {
            dropped = _deferred.TryRemove(new KeyValuePair<string, Deferred>(key, deferred));
            if (dropped && state is not null && _view is not null)
            {
                Replace(ApplyPending(state, read));
            }
        }

        Dispatch();
        return dropped;
    }

    // Writes one change; on success, records which reads may still miss it.
    private async Task<Write> TryWriteAsync(string key, Deferred deferred)
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
                    return Write.Pending;   // tried again at the next refresh
                }

                write = _writes.Start(key, () => deferred.Change(_store, CancellationToken.None).AsTask());
                deferred.Writing = write;
            }

            applied = await write.WaitAsync(WriteTimeout).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (deferred.Limit is not null)
        {
            // How a store refuses a change over its limit (IHomeostatStore.TrySetGroupPauseAsync); the caller confirms it
            // with a read, since a store or its client may throw the same type in an outage.
            _logger.LogDebug(ex, "Writing a Homeostat change ({Change}) failed; checking the store's limit.", Safe(key));
            deferred.LimitError = ex;
            return Write.OverLimit;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Writing a Homeostat change ({Change}) failed.", Safe(key));
            return Write.Pending;
        }

        if (!applied)
        {
            // Refused: the store holds a later decision about the same thing (taken elsewhere meanwhile), or already
            // has this change (a release written twice, by the caller and by a refresh). Either way the store is right:
            // kept as written until a read that starts from now on shows what the store has, so an older read cannot
            // make the view flap. The caller (or the refresh) reports it: a later decision is logged as a warning.
            deferred.Refused = true;
        }

        // A read that starts from now on sees it; earlier ones may not.
        deferred.WrittenBefore = Volatile.Read(ref _reads) + 1;
        return Write.Answered;
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
                if (_deferred.TryRemove(new KeyValuePair<string, Deferred>(key, deferred))
                    && deferred.IsSafe && deferred.WrittenBefore is null && read > 0)
                {
                    // Never written: the store holds a later decision, as when it refuses the write.
                    ReportRefused(key, deferred, state);
                }

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
            var written = await TryWriteAsync(key, deferred).ConfigureAwait(false);
            if (written == Write.OverLimit && read > 0 && deferred.Limit?.Invoke(state) is { } refused)
            {
                // The state just read confirms the store's refusal (a pause deferred while the store was down, or made
                // from a view behind the store): dropped, as a change refused for a later decision is (S2); the refresh
                // rebuilds the view without it.
                if (Drop(key, deferred, state: null, read))
                {
                    _logger.LogWarning(
                        "The Homeostat store refused a change ({Change}) decided at {DecidedAt:O}: {Reason} It no longer applies on this instance.",
                        Safe(key), deferred.DecidedAt, refused);
                }

                return;
            }

            if (written != Write.Answered)
            {
                return;   // still pending (an unconfirmed limit included: the state could not be read)
            }

            if (deferred.Refused)
            {
                // Off the refresh, which must not wait for one more read.
                _ = Task.Run(() => ReportRefusedAsync(key, deferred), CancellationToken.None);
            }
            else
            {
                _logger.LogInformation("A deferred Homeostat change ({Change}) was written to the store.", Safe(key));
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

        /// <summary>When the change was decided (the store orders decisions by it).</summary>
        public DateTimeOffset DecidedAt { get; init; }

        /// <summary>A change in the safe direction (a pause, a switch off, a release), as opposed to one that lets Homeostat act.</summary>
        public bool IsSafe { get; init; }

        /// <summary>Describes the later decision the store shows when it refuses this change (see ChangeOrDeferAsync).</summary>
        public Func<ControlState, string?>? Later { get; init; }

        /// <summary>The store refused the change: it holds a later decision.</summary>
        public volatile bool Refused;

        /// <summary>1 once a refusal was reported.</summary>
        public int Reported;

        /// <summary>A limit of the store that may refuse the change (see ChangeOrDeferAsync), or <c>null</c>.</summary>
        public Func<ControlState, string?>? Limit { get; init; }

        /// <summary>The store's error when it may have refused the change for its <see cref="Limit"/>.</summary>
        public volatile Exception? LimitError;
    }

    /// <summary>
    /// Applies a change that lets Homeostat act (a resume, a switch turned on, a claim) to the store; store failures reach
    /// the caller. Once the store took it, <paramref name="apply"/> updates this instance's view at once, and keeps
    /// applying on top of reads that may have started before the write, so the view never flaps back.
    /// </summary>
    /// <param name="key">What the change is about ("pause", "switch:{name}"): an older pending change of the same thing is
    /// held back while this one is written, and dropped once the store takes this one.</param>
    /// <param name="decidedAt">When the change was decided: a change in the safe direction decided at or after it stands.</param>
    /// <param name="change">Writes the change; <c>false</c> when the store refused it (a later decision, or another
    /// owner): then nothing changes here, and an older pending change stands.</param>
    /// <param name="apply">The same change, applied to this instance's view.</param>
    /// <param name="cancellationToken">Cancels the wait for the write.</param>
    /// <returns>Whether the store took the change.</returns>
    public async Task<bool> ChangeAsync(
        string key,
        DateTimeOffset decidedAt,
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
        var written = new Deferred((_, _) => ValueTask.FromResult(true), apply, static _ => false) { WrittenBefore = Volatile.Read(ref _reads) + 1, DecidedAt = decidedAt };
        lock (_viewGate)
        {
            // A change in the safe direction made while this write ran (a pause during a resume) is the later decision:
            // it stays pending and shown, and reaches the store after this one. Otherwise this change is shown.
            _deferred.TryGetValue(key, out var pending);

            // A change in the safe direction decided at or after this one stays shown even once written (the store took
            // it after this write, or refused this write for it): an older resume or turn-on must not hide a newer pause
            // or turn-off until the next read. On a tie, doing less wins, as in the store (S2).
            // (One the store refused holds nothing newer than what the store has: it does not count.)
            if (pending is { IsSafe: true, Refused: false } && pending.DecidedAt >= decidedAt)
            {
                if (ReferenceEquals(pending, older))
                {
                    older.Withdrawn = false;   // decided after this change: still to be written, after it
                }
            }
            // (A change already written, by a concurrent call of this method, is not newer: this one is shown.)
            else if (pending is null || ReferenceEquals(pending, older) || pending.WrittenBefore is not null)
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
        => new() { Pause = pause, GroupPauses = state.GroupPauses, Switches = state.Switches, Approvals = state.Approvals };

    /// <summary>A copy of the state with another pause of a group (removed with <c>null</c>).</summary>
    public static ControlState WithGroupPause(ControlState state, string group, PauseState? pause)
    {
        var pauses = state.GroupPauses.Where(p => !string.Equals(p.Group, group, StringComparison.Ordinal));
        if (pause is not null)
        {
            pauses = pauses.Append(GroupPauseState.From(group, pause));
        }

        return new() { Pause = state.Pause, GroupPauses = [.. pauses.OrderBy(p => p.Group, StringComparer.Ordinal)], Switches = state.Switches, Approvals = state.Approvals };
    }

    /// <summary>The pause of a group in a state, or <c>null</c>.</summary>
    public static PauseState? GroupPauseOf(ControlState state, string group)
        => state.GroupPauses.FirstOrDefault(p => string.Equals(p.Group, group, StringComparison.Ordinal))?.ToPause();

    /// <summary>A copy of the state with a switch turned on (replacing its previous state) or, with <c>null</c>, off.</summary>
    public static ControlState WithSwitch(ControlState state, string name, SwitchState? value)
    {
        var switches = state.Switches.Where(s => !string.Equals(s.Name, name, StringComparison.Ordinal));
        if (value is not null)
        {
            switches = switches.Append(value);
        }

        return new() { Pause = state.Pause, GroupPauses = state.GroupPauses, Switches = [.. switches.OrderBy(s => s.Name, StringComparer.Ordinal)], Approvals = state.Approvals };
    }

    private static ControlState Validate(ControlState? state)
    {
        // A custom store returning an incomplete state is treated like a failed read, not trusted.
        // A group pause without its group could be a person's pause of any group: not trusted either (fail closed).
        if (state?.Switches is null || state.Approvals is null || state.GroupPauses is null
            || state.Switches.Any(s => s?.Name is null) || state.Approvals.Any(a => a?.Request is null)
            || state.GroupPauses.Any(p => p?.Group is null))
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

        // Read for every triggering signal of a condition in a group: a lookup, not a scan. (A store returning two pauses
        // of one group: the later decision counts.)
        public Dictionary<string, GroupPauseState> GroupPauses { get; } = state.GroupPauses
            .GroupBy(p => p.Group, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.MaxBy(p => p.At)!, StringComparer.Ordinal);
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
