using System.Collections.Concurrent;

namespace Homeostat;

internal sealed class HomeostatSwitches : IHomeostatSwitches
{
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);

    public bool IsOn(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _active.ContainsKey(name);
    }

    public void TurnOn(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _active.TryAdd(name, 0);
    }

    public void TurnOff(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _active.TryRemove(name, out _);
    }

    public IReadOnlyCollection<string> GetActive() => _active.Keys.ToArray();
}
