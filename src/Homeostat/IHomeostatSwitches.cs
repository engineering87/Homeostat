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
    /// <summary>Whether the switch is on. Unknown switches are off.</summary>
    bool IsOn(string name);

    /// <summary>Turns the switch on. Turning on a switch that is already on has no effect.</summary>
    void TurnOn(string name);

    /// <summary>Turns the switch off, for example once the dependency has recovered.</summary>
    void TurnOff(string name);

    /// <summary>Names of the switches currently on.</summary>
    IReadOnlyCollection<string> GetActive();

    /// <summary>
    /// Raised after a switch is turned on or off (not when the call leaves it unchanged). Handlers run synchronously
    /// on the thread that changed the switch, so keep them quick; an exception thrown by a handler is logged and does
    /// not reach the caller. Events for the same switch can arrive out of order under concurrent changes: read
    /// <see cref="IsOn"/> in the handler when the current state matters.
    /// </summary>
    event EventHandler<SwitchChangedEventArgs>? Changed;
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
