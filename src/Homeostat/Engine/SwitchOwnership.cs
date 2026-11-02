namespace Homeostat.Engine;

/// <summary>
/// Remembers which conditions turned on which resettable switches, so a switch is turned off only when the last
/// condition that needs it clears, and never one that Homeostat did not turn on.
/// </summary>
internal sealed class SwitchOwnership
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _owners = new(StringComparer.Ordinal);

    public void Claim(string switchName, string condition)
    {
        lock (_gate)
        {
            if (!_owners.TryGetValue(switchName, out var owners))
            {
                owners = new HashSet<string>(StringComparer.Ordinal);
                _owners[switchName] = owners;
            }

            owners.Add(condition);
        }
    }

    /// <summary>Releases every claim of the condition; returns the switches that no condition holds any more.</summary>
    public IReadOnlyList<string> Release(string condition)
    {
        var free = new List<string>();
        lock (_gate)
        {
            foreach (var (switchName, owners) in _owners)
            {
                if (owners.Remove(condition) && owners.Count == 0)
                {
                    free.Add(switchName);
                }
            }

            foreach (var switchName in free)
            {
                _owners.Remove(switchName);
            }
        }

        return free;
    }
}
