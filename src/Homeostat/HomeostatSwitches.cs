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
    private readonly SharedControl _shared;
    private readonly TimeProvider _timeProvider;
    private readonly string _instance;
    private readonly ILogger _logger;

    public HomeostatSwitches(SharedControl shared, TimeProvider timeProvider, IOptions<HomeostatOptions> options, ILogger<HomeostatSwitches>? logger = null)
    {
        _shared = shared;
        _timeProvider = timeProvider;
        _instance = options.Value.InstanceId;
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

        // Always written: this instance's view may not show yet a switch turned on elsewhere a moment ago.
        await _shared.ChangeAsync(
            (store, ct) => store.SetSwitchAsync(name, null, ct), s => SharedControl.WithSwitch(s, name, null), cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Switch {Switch} turned off{By}.", name.ReplaceLineEndings(" "), Who(by));
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
        var state = new SwitchState { Name = name, Condition = condition, Instance = _instance, At = _timeProvider.GetUtcNow() };
        var claimed = false;
        await _shared.ChangeAsync(
            async (store, ct) => claimed = await store.TryClaimSwitchAsync(state, ct).ConfigureAwait(false),
            s => claimed ? SharedControl.WithSwitch(s, name, state) : s,
            cancellationToken).ConfigureAwait(false);
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
        var state = await _shared.Store.GetControlStateAsync(cancellationToken).ConfigureAwait(false);
        var owned = state.Switches
            .Where(s => string.Equals(s.Condition, condition, StringComparison.Ordinal) && (anyInstance || string.Equals(s.Instance, _instance, StringComparison.Ordinal)))
            .Select(s => s.Name)
            .ToArray();
        if (owned.Length == 0)
        {
            return [];
        }

        var released = new List<string>();
        await _shared.ChangeAsync(
            async (store, ct) =>
            {
                foreach (var name in owned)
                {
                    if (await store.TryReleaseSwitchAsync(name, condition, anyInstance ? null : _instance, ct).ConfigureAwait(false))
                    {
                        released.Add(name);
                    }
                }
            },
            s => released.Aggregate(s, (current, name) => SharedControl.WithSwitch(current, name, null)),
            cancellationToken).ConfigureAwait(false);
        return released;
    }

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
            (store, ct) => store.SetSwitchAsync(name, state, ct), s => SharedControl.WithSwitch(s, name, state), cancellationToken).ConfigureAwait(false);
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
