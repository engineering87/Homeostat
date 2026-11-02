using System.Collections.Concurrent;

namespace Homeostat.Diagnostics;

/// <summary>When each condition became active, in this process (the latest 1000 per condition), for coverage reports.</summary>
internal sealed class ActivationLog
{
    private const int Capacity = 1000;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _activations = new(StringComparer.Ordinal);

    public void Record(string condition, DateTimeOffset at)
    {
        var queue = _activations.GetOrAdd(condition, static _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            queue.Enqueue(at);
            if (queue.Count > Capacity)
            {
                queue.Dequeue();
            }
        }
    }

    public IReadOnlyList<DateTimeOffset> Since(string condition, DateTimeOffset since)
    {
        if (!_activations.TryGetValue(condition, out var queue))
        {
            return [];
        }

        lock (queue)
        {
            return queue.Where(at => at >= since).ToArray();
        }
    }
}
