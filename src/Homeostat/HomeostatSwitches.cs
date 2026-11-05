using System.Collections.Concurrent;
using Homeostat.Engine;
using Homeostat.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Homeostat;

/// <summary>
/// Instance switches in memory, application switches in the store (through <see cref="SharedControl"/>). A switch is
/// on when it is on in either scope.
/// </summary>
internal sealed class HomeostatSwitches : IHomeostatSwitches
{
    private readonly ConcurrentDictionary<string, byte> _local = new(StringComparer.Ordinal);

    // Application switches that a cluster plan of this process claimed and still holds: renewed while held, so other
    // instances can tell a live owner from one that stopped or crashed.
    private readonly ConcurrentDictionary<string, string> _claimed = new(StringComparer.Ordinal);
    private readonly SharedControl _shared;
    private readonly TimeProvider _timeProvider;
    private readonly string _instance;
    private readonly TimeSpan _renewEvery;
    private readonly TimeSpan _orphanAfter;
    private readonly Conditions.ConditionSet? _conditions;
    private readonly ILogger _logger;

    public HomeostatSwitches(
        SharedControl shared,
        TimeProvider timeProvider,
        IOptions<HomeostatOptions> options,
        ILogger<HomeostatSwitches>? logger = null,
        Conditions.ConditionSet? conditions = null)
    {
        _shared = shared;
        _timeProvider = timeProvider;
        _instance = options.Value.InstanceId;
        _conditions = conditions;
        _renewEvery = options.Value.ControlRefreshInterval * 3;
        _orphanAfter = _renewEvery * 4 > TimeSpan.FromMinutes(1) ? _renewEvery * 4 : TimeSpan.FromMinutes(1);
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _shared.Refreshed += OnRefreshed;
    }

    public event EventHandler<SwitchChangedEventArgs>? Changed;

    public bool IsOn(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _local.ContainsKey(name) || _shared.IsSwitchOn(name);
    }

    public ValueTask TurnOnAsync(string name, SwitchScope scope = SwitchScope.Application, string? by = null, CancellationToken cancellationToken = default)
        => scope switch
        {
            SwitchScope.Application => SetSharedAsync(name, by, cancellationToken),
            SwitchScope.Instance => TurnOnLocal(name),
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown switch scope."),
        };

    public async ValueTask TurnOffAsync(string name, string? by = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        TurnOffLocal(name);
        _claimed.TryRemove(name, out _);

        // Always written: this instance's view may not show yet a switch turned on elsewhere a moment ago. Turning off
        // is the safe direction: when the store cannot take it, it applies here at once and is written when it can, so
        // a store outage never keeps chaos, an isolated circuit or a paused consumer on.
        var intended = _timeProvider.GetUtcNow();
        var written = await _shared.ChangeOrDeferAsync(
            Key(name),
            (store, ct) => store.TrySetSwitchAsync(name, null, intended, ct),
            s => SharedControl.WithSwitch(s, name, null),
            s => s.Switches.Any(x => string.Equals(x.Name, name, StringComparison.Ordinal) && x.At > intended)).ConfigureAwait(false);
        _logger.LogInformation("Switch {Switch} turned off{By}{Deferred}.", name.ReplaceLineEndings(" "), Who(by),
            written ? string.Empty : " on this instance; the store will take it when it answers");
    }

    public IReadOnlyCollection<string> GetActive()
        => _local.Keys.Concat(_shared.Current.Switches.Select(s => s.Name)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Turns on an application switch on behalf of a condition's cluster plan, which owns it until the condition clears
    /// on this instance. The store decides atomically: a switch a person turned on, or another condition owns, stays theirs.
    /// </summary>
    internal async ValueTask TurnOnForConditionAsync(string name, string condition, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var now = _timeProvider.GetUtcNow();
        var state = new SwitchState { Name = name, Condition = condition, Instance = _instance, At = now, RenewedAt = now };
        var claimed = 0;
        var gaveUp = 0;
        try
        {
            await _shared.ChangeAsync(
                Key(name),
                async (store, ct) =>
                {
                    if (!await store.TryClaimSwitchAsync(state, ct).ConfigureAwait(false))
                    {
                        // Owned by a person or another condition: theirs, and nothing changes here.
                        return false;
                    }

                    Interlocked.Exchange(ref claimed, 1);

                    // The step already gave up (it timed out, or the run stopped) while the store was slow: nobody
                    // would own or limit this switch, so it is released at once.
                    if (Volatile.Read(ref gaveUp) == 1)
                    {
                        await store.TryReleaseSwitchAsync(name, condition, _instance, _timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
                        return false;
                    }

                    return true;
                },
                s => SharedControl.WithSwitch(s, name, state),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref gaveUp, 1);
            if (Volatile.Read(ref claimed) == 1)
            {
                await ReleaseAsync(name, condition, _instance, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        if (Volatile.Read(ref claimed) == 1)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // Claimed, but too late for the step, which is reported as failed: released, not left on unowned.
                await ReleaseAsync(name, condition, _instance, CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }

            _claimed[name] = condition;
        }
    }

    /// <summary>The application switch as this instance's view has it, or <c>null</c>.</summary>
    internal SwitchState? GetShared(string name)
        => _shared.Current.Switches.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// Turns off the application switches owned by a condition (its cluster plan turned them on): only those this
    /// instance turned on, or, with <paramref name="anyInstance"/> (the condition was removed), all of them. Reads the
    /// store, not the view, which may not show them yet.
    /// </summary>
    internal async ValueTask<IReadOnlyList<string>> ReleaseSharedAsync(string condition, CancellationToken cancellationToken, bool anyInstance = false)
    {
        // The store, which may show switches the view does not have yet; the view when the store does not answer.
        ControlState state;
        try
        {
            state = await SharedControl.Bounded(() => _shared.Store.GetControlStateAsync(cancellationToken), SharedControl.ReadTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            state = _shared.Current;
        }

        var instance = anyInstance ? null : _instance;
        var owned = state.Switches
            .Where(s => string.Equals(s.Condition, condition, StringComparison.Ordinal) && (instance is null || string.Equals(s.Instance, instance, StringComparison.Ordinal)))
            .Select(s => s.Name)
            .Concat(_claimed.Where(c => string.Equals(c.Value, condition, StringComparison.Ordinal)).Select(c => c.Key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (var name in owned)
        {
            await ReleaseAsync(name, condition, instance, cancellationToken).ConfigureAwait(false);
        }

        return owned;
    }

    /// <summary>Releases one application switch if this instance's plan for the condition still holds it.</summary>
    internal async Task<bool> ReleaseOwnedAsync(string name, string condition, CancellationToken cancellationToken, DateTimeOffset? claimedAt = null)
    {
        if (GetShared(name) is not { } current || !string.Equals(current.Condition, condition, StringComparison.Ordinal)
            || !string.Equals(current.Instance, _instance, StringComparison.Ordinal)
            || (claimedAt is { } at && current.At != at))
        {
            return false;
        }

        await ReleaseAsync(name, condition, _instance, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Releases an application switch the condition owns (turned on by <paramref name="instance"/>, or any instance):
    /// written at once, or applied here and written when the store answers. Never turns off a switch that a person or
    /// another plan has taken since.
    /// </summary>
    private async Task ReleaseAsync(string name, string condition, string? instance, CancellationToken cancellationToken, DateTimeOffset? decidedAt = null)
    {
        // Decided now (or as of the renewal the caller saw): written in that order even if it reaches the store late, so
        // it never removes a newer claim, or a claim renewed since.
        var at = decidedAt ?? _timeProvider.GetUtcNow();
        _claimed.TryRemove(new KeyValuePair<string, string>(name, condition));
        bool Owns(SwitchState s) => string.Equals(s.Name, name, StringComparison.Ordinal)
            && string.Equals(s.Condition, condition, StringComparison.Ordinal)
            && (instance is null || string.Equals(s.Instance, instance, StringComparison.Ordinal));

        await _shared.ChangeOrDeferAsync(
            Key(name),
            (store, ct) => store.TryReleaseSwitchAsync(name, condition, instance, at, ct),
            s => s.Switches.Any(Owns) ? SharedControl.WithSwitch(s, name, null) : s,
            // Superseded only by a later decision about the switch (claimed again, or taken by a person), never because
            // a view does not show it: this instance's own view already has the release applied.
            s => s.Switches.Any(x => string.Equals(x.Name, name, StringComparison.Ordinal) && x.At > at)).ConfigureAwait(false);
    }

    /// <summary>
    /// After each refresh: renews the application switches this process holds, and releases those whose owner stopped
    /// or crashed (no renewal for a while) when their condition is not active here. Without this, a switch turned on by a
    /// cluster plan would stay on for every instance once the instance that turned it on is gone.
    /// </summary>
    internal async Task MaintainOwnershipAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var current in _shared.Current.Switches)
        {
            if (current.Condition is not { } condition)
            {
                continue;
            }

            var renewed = current.RenewedAt ?? current.At;
            try
            {
                if (string.Equals(current.Instance, _instance, StringComparison.Ordinal)
                    && _claimed.TryGetValue(current.Name, out var holder) && string.Equals(holder, condition, StringComparison.Ordinal))
                {
                    if (now - renewed >= _renewEvery)
                    {
                        // One renewal at a time per switch: while one abandoned at its timeout still runs (a store blocked
                        // on I/O), the next refresh does not start another.
                        if (_renewing.IsRunning(current.Name))
                        {
                            continue;
                        }

                        var renewal = new SwitchState { Name = current.Name, Condition = condition, Instance = _instance, By = current.By, At = current.At, RenewedAt = now };
                        var name = current.Name;
                        var renewing = _renewing.Start(name, () => _shared.Store.TryClaimSwitchAsync(renewal, CancellationToken.None).AsTask());

                        // Refused: the claim is gone (released meanwhile, or taken over): this instance no longer holds it.
                        if (!await renewing.WaitAsync(SharedControl.WriteTimeout, cancellationToken).ConfigureAwait(false))
                        {
                            _claimed.TryRemove(new KeyValuePair<string, string>(name, condition));
                        }
                    }
                }
                else if (now - renewed >= _orphanAfter && _conditions?.Get(condition)?.Status != Conditions.ConditionStatus.Active)
                {
                    _logger.LogWarning(
                        "Application switch {Switch} was turned on by condition {Condition} on instance {Instance}, which no longer holds it, "
                        + "and the condition is not active here: turning it off.", current.Name, condition, current.Instance);
                    // As of the renewal seen here: if its owner renewed it since (this view is a few seconds old), the
                    // store keeps it. An abandoned switch (its owner stopped) is released as of now.
                    var seen = current.RenewedAt is { } r && r > DateTimeOffset.UnixEpoch ? r : (DateTimeOffset?)null;
                    await ReleaseAsync(current.Name, condition, current.Instance, cancellationToken, seen ?? now).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Could not renew or release application switch {Switch}.", current.Name);
            }
        }
    }

    /// <summary>
    /// When this process stops: its switches are marked as no longer held, so another instance releases them as soon
    /// as their condition is not active there, instead of waiting for the renewal to lapse.
    /// </summary>
    internal async Task AbandonOwnershipAsync(CancellationToken cancellationToken)
    {
        foreach (var (name, condition) in _claimed)
        {
            if (GetShared(name) is not { } current || !string.Equals(current.Instance, _instance, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var abandoned = new SwitchState { Name = name, Condition = condition, Instance = _instance, By = current.By, At = current.At, RenewedAt = DateTimeOffset.MinValue };
                await SharedControl.Bounded(() => _shared.Store.TryClaimSwitchAsync(abandoned, cancellationToken), TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The renewal lapses anyway: the switch is released a little later.
            }
        }

        _claimed.Clear();
    }

    private readonly InFlight<bool> _renewing = new();

    private static string Key(string name) => "switch:" + name;

    internal ValueTask TurnOnLocal(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // On for the whole application by a person's decision: a local copy would outlive their turning it off.
        if (GetShared(name) is { Condition: null })
        {
            return ValueTask.CompletedTask;
        }

        var wasOn = IsOn(name);
        if (_local.TryAdd(name, 0) && !wasOn)
        {
            Raise(name, isOn: true);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Turns off the instance switch; returns whether it was on for this instance.</summary>
    internal bool TurnOffLocal(string name)
    {
        if (!_local.TryRemove(name, out _))
        {
            return false;
        }

        if (!_shared.IsSwitchOn(name))
        {
            Raise(name, isOn: false);
        }

        return true;
    }

    private async ValueTask SetSharedAsync(string name, string? by, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // A person or code turning on a switch makes it theirs, even one a plan owns: the condition clearing no longer
        // turns it off. Always written: this instance's view may be behind (the switch turned off elsewhere a moment ago).
        var state = new SwitchState { Name = name, By = by, At = _timeProvider.GetUtcNow() };
        var wasOff = GetShared(name) is null;
        bool applied;
        try
        {
            applied = await _shared.ChangeAsync(
                Key(name),
                (store, ct) => store.TrySetSwitchAsync(name, state, state.At, ct),
                s => SharedControl.WithSwitch(s, name, state),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && wasOff)
        {
            // The caller is told it failed, but the store may still run the write later: the switch is turned off
            // again, decided after it, so it ends off, as the caller was told.
            var off = _timeProvider.GetUtcNow();
            await _shared.ChangeOrDeferAsync(
                Key(name),
                (store, ct) => store.TrySetSwitchAsync(name, null, off, ct),
                s => SharedControl.WithSwitch(s, name, null),
                s => s.Switches.Any(x => string.Equals(x.Name, name, StringComparison.Ordinal) && x.At > off)).ConfigureAwait(false);
            throw;
        }

        if (!applied)
        {
            _logger.LogWarning("Switch {Switch} was not turned on: a later decision about it is in the store.", name.ReplaceLineEndings(" "));
            return;
        }

        _claimed.TryRemove(name, out _);
    }

    private void OnRefreshed(ControlState previous, ControlState next)
    {
        var before = previous.Switches.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var after = next.Switches.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in after.Except(before))
        {
            if (!_local.ContainsKey(name))
            {
                Raise(name, isOn: true);
            }
        }

        foreach (var name in before.Except(after))
        {
            if (!_local.ContainsKey(name))
            {
                Raise(name, isOn: false);
            }
        }
    }

    private static string Who(string? by) => by is { Length: > 0 } who ? $" by {who.ReplaceLineEndings(" ")}" : string.Empty;

    private void Raise(string name, bool isOn)
    {
        if (Changed is not { } handlers)
        {
            return;
        }

        var args = new SwitchChangedEventArgs(name, isOn);
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((EventHandler<SwitchChangedEventArgs>)handler)(this, args);
            }
            catch (Exception ex)
            {
                // The name may come from an HTTP request: a line break must not start a fake log entry.
                _logger.LogError(ex, "A handler of switch {Switch} failed.", name.ReplaceLineEndings(" "));
            }
        }
    }
}
