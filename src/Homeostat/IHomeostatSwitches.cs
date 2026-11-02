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
}
