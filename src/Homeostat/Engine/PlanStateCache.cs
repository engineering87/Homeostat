using System.Collections.Concurrent;
using Homeostat.Persistence;

namespace Homeostat.Engine;

/// <summary>
/// Last known state of each plan, so triggering signals during a cooldown or a hold do not hit the store each time.
/// Shared by the runner and the control API, which forgets an entry when it changes the state behind the runner.
/// </summary>
internal sealed class PlanStateCache
{
    public ConcurrentDictionary<string, PlanState> Known { get; } = new(StringComparer.Ordinal);

    /// <summary>When a hold was last read from the store: a cached hold is trusted only for a while.</summary>
    public ConcurrentDictionary<string, DateTimeOffset> HeldReadAt { get; } = new(StringComparer.Ordinal);

    public void Forget(string key)
    {
        Known.TryRemove(key, out _);
        HeldReadAt.TryRemove(key, out _);
    }
}
