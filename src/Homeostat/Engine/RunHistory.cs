using Homeostat.Healing;
using Microsoft.Extensions.Options;

namespace Homeostat.Engine;

/// <summary>Keeps the most recent run results in memory.</summary>
internal sealed class RunHistory
{
    private readonly object _gate = new();
    private readonly Queue<HealingRunResult> _runs = new();
    private readonly int _capacity;

    public RunHistory(IOptions<HomeostatOptions> options) => _capacity = options.Value.RunHistorySize;

    public void Add(HealingRunResult run)
    {
        lock (_gate)
        {
            _runs.Enqueue(run);
            while (_runs.Count > _capacity)
            {
                _runs.Dequeue();
            }
        }
    }

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<HealingRunResult> GetRecent()
    {
        lock (_gate)
        {
            return _runs.Reverse().ToArray();
        }
    }
}
