using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homeostat;

internal sealed class HomeostatSwitches : IHomeostatSwitches
{
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public HomeostatSwitches(ILogger<HomeostatSwitches>? logger = null)
        => _logger = (ILogger?)logger ?? NullLogger.Instance;

    public event EventHandler<SwitchChangedEventArgs>? Changed;

    public bool IsOn(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _active.ContainsKey(name);
    }

    public void TurnOn(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_active.TryAdd(name, 0))
        {
            Raise(name, isOn: true);
        }
    }

    public void TurnOff(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_active.TryRemove(name, out _))
        {
            Raise(name, isOn: false);
        }
    }

    public IReadOnlyCollection<string> GetActive() => _active.Keys.ToArray();

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
                _logger.LogError(ex, "A handler of switch {Switch} failed.", name);
            }
        }
    }
}
