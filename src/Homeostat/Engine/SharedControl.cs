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

    private readonly IHomeostatStore _store;
    private readonly ILogger<SharedControl> _logger;
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly ConcurrentQueue<(ControlState Previous, ControlState Next)> _changes = new();
    private int _dispatching;

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
                next = Validate(await _store.GetControlStateAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not read the Homeostat control state from the store; keeping the last known state.");
                return false;
            }

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
    /// Applies a change to the store, then refreshes the view. Store failures reach the caller. When the change was
    /// written but the state cannot be read back, <paramref name="apply"/> updates the view here, so a change made on
    /// this instance applies to it at once all the same.
    /// </summary>
    public async Task ChangeAsync(
        Func<IHomeostatStore, CancellationToken, ValueTask> change,
        Func<ControlState, ControlState>? apply,
        CancellationToken cancellationToken)
    {
        await change(_store, cancellationToken).ConfigureAwait(false);

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
internal sealed class SharedControlRefresher(SharedControl control, IOptions<HomeostatOptions> options, TimeProvider timeProvider) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // The guardrails are fixed from now on (see HomeostatOptions.Freeze).
        options.Value.Freeze();

        // A pause set before this instance started applies from its first signal. Until the state is read, Homeostat
        // counts as paused (see HomeostatControl.PauseReason), so a store unreachable at startup delays healing
        // rather than letting it run through a pause it cannot see.
        await control.TryRefreshAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
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
                await control.TryRefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
