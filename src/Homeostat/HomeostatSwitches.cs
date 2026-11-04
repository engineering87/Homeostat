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
            (store, ct) => store.SetSwitchAsync(name, null, ct),
            s => SharedControl.WithSwitch(s, name, null),
            s => s.Switches.Any(x => string.Equals(x.Name, name, StringComparison.Ordinal) && x.At > intended),
            cancellationToken).ConfigureAwait(false);
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
        var claimed = false;
        await _shared.ChangeAsync(
            async (store, ct) => claimed = await store.TryClaimSwitchAsync(state, ct).ConfigureAwait(false),
            s => claimed ? SharedControl.WithSwitch(s, name, state) : s,
            cancellationToken,
            Key(name)).ConfigureAwait(false);
        if (claimed)
        {
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
    internal async Task<bool> ReleaseOwnedAsync(string name, string condition, CancellationToken cancellationToken)
    {
        if (GetShared(name) is not { } current || !string.Equals(current.Condition, condition, StringComparison.Ordinal)
            || !string.Equals(current.Instance, _instance, StringComparison.Ordinal))
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
    private async Task ReleaseAsync(string name, string condition, string? instance, CancellationToken cancellationToken)
    {
        _claimed.TryRemove(new KeyValuePair<string, string>(name, condition));
        bool Owns(SwitchState s) => string.Equals(s.Name, name, StringComparison.Ordinal)
            && string.Equals(s.Condition, condition, StringComparison.Ordinal)
            && (instance is null || string.Equals(s.Instance, instance, StringComparison.Ordinal));

        await _shared.ChangeOrDeferAsync(
            Key(name),
            async (store, ct) => await store.TryReleaseSwitchAsync(name, condition, instance, ct).ConfigureAwait(false),
            s => s.Switches.Any(Owns) ? SharedControl.WithSwitch(s, name, null) : s,
            s => !s.Switches.Any(Owns),
            cancellationToken).ConfigureAwait(false);
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
                        var renewal = new SwitchState { Name = current.Name, Condition = condition, Instance = _instance, By = current.By, At = current.At, RenewedAt = now };
                        await SharedControl.Bounded(() => _shared.Store.TryClaimSwitchAsync(renewal, cancellationToken), SharedControl.WriteTimeout, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                else if (now - renewed >= _orphanAfter && _conditions?.Get(condition)?.Status != Conditions.ConditionStatus.Active)
                {
                    _logger.LogWarning(
                        "Application switch {Switch} was turned on by condition {Condition} on instance {Instance}, which no longer holds it, "
                        + "and the condition is not active here: turning it off.", current.Name, condition, current.Instance);
                    await ReleaseAsync(current.Name, condition, current.Instance, cancellationToken).ConfigureAwait(false);
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

    internal void TurnOffLocal(string name)
    {
        if (_local.TryRemove(name, out _) && !_shared.IsSwitchOn(name))
        {
            Raise(name, isOn: false);
        }
    }

    private async ValueTask SetSharedAsync(string name, string? by, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // A person or code turning on a switch makes it theirs, even one a plan owns: the condition clearing no longer
        // turns it off. Already theirs: nothing to change.
        if (GetShared(name) is { Condition: null })
        {
            return;
        }

        var state = new SwitchState { Name = name, By = by, At = _timeProvider.GetUtcNow() };
        await _shared.ChangeAsync(
            (store, ct) => store.SetSwitchAsync(name, state, ct), s => SharedControl.WithSwitch(s, name, state), cancellationToken, Key(name)).ConfigureAwait(false);
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
