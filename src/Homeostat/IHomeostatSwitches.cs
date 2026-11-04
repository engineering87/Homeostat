namespace Homeostat;

/// <summary>
/// Named on/off switches that healing plans turn on and your code reads, for example to serve cached
/// data or to disable a non-essential feature while a dependency is down.
/// </summary>
/// <example>
/// <code>
/// if (switches.IsOn("serve-cached-forecast"))
/// {
///     return cache.GetLastForecast();
/// }
/// </code>
/// </example>
public interface IHomeostatSwitches
{
    /// <summary>Whether the switch is on, for the application or for this instance. Unknown switches are off.</summary>
    /// <remarks>Reads this instance's view of the store and never waits: cheap enough for every request.</remarks>
    bool IsOn(string name);

    /// <summary>
    /// Turns the switch on. With <see cref="SwitchScope.Application"/> (the default) the switch is kept in the store,
    /// so with a shared store it is on for every instance within <see cref="HomeostatOptions.ControlRefreshInterval"/>
    /// and survives restarts; with <see cref="SwitchScope.Instance"/> it is on for this instance only, until it stops.
    /// Turning on a switch that is already on has no effect.
    /// </summary>
    /// <param name="name">Name of the switch.</param>
    /// <param name="scope">For the whole application or for this instance.</param>
    /// <param name="by">Who turned it on, recorded with the switch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask TurnOnAsync(string name, SwitchScope scope = SwitchScope.Application, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns the switch off, for the application and for this instance (a switch that another instance turned on for
    /// itself stays on there).
    /// </summary>
    /// <param name="name">Name of the switch.</param>
    /// <param name="by">Who turned it off, recorded in the log.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask TurnOffAsync(string name, string? by = null, CancellationToken cancellationToken = default);

    /// <summary>Names of the switches currently on, for the application or for this instance.</summary>
    IReadOnlyCollection<string> GetActive();

    /// <summary>
    /// Raised after a switch is turned on or off, here or (for application switches) on another instance, and not
    /// when nothing changed. Handlers run synchronously, so keep them quick; an exception thrown by a handler is logged
    /// and does not reach the caller. Events for the same switch can arrive out of order under concurrent changes:
    /// read <see cref="IsOn"/> in the handler when the current state matters.
    /// </summary>
    event EventHandler<SwitchChangedEventArgs>? Changed;
}

/// <summary>Where a switch is on.</summary>
/// <remarks>Numbered from 1: the default value, 0, is none of them.</remarks>
public enum SwitchScope
{
    /// <summary>For the whole application: kept in the store, shared by every instance with a shared store.</summary>
    Application = 1,

    /// <summary>For this instance only, in its memory.</summary>
    Instance = 2,
}

/// <summary>Data for <see cref="IHomeostatSwitches.Changed"/>.</summary>
/// <param name="name">Name of the switch.</param>
/// <param name="isOn">Whether the change turned the switch on.</param>
public sealed class SwitchChangedEventArgs(string name, bool isOn) : EventArgs
{
    /// <summary>Name of the switch.</summary>
    public string Name { get; } = name;

    /// <summary>Whether the change turned the switch on.</summary>
    public bool IsOn { get; } = isOn;
}
