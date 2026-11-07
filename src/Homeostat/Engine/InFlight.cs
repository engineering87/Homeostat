using System.Collections.Concurrent;

namespace Homeostat.Engine;

/// <summary>
/// Calls that may outlive the wait for them (a store or an action that does not return in time), one per key: while one
/// still runs, another is not started for the same key, so a call that blocks cannot pile up threads by being retried.
/// Entries go away when their call ends.
/// </summary>
internal sealed class InFlight<T>
{
    private readonly ConcurrentDictionary<string, Task<T>> _calls = new(StringComparer.Ordinal);

    /// <summary>Whether a call for <paramref name="key"/> is still running.</summary>
    public bool IsRunning(string key) => _calls.TryGetValue(key, out var call) && !call.IsCompleted;

    /// <summary>Starts <paramref name="call"/> on the thread pool (never on the caller's thread) and tracks it.</summary>
    public Task<T> Start(string key, Func<Task<T>> call)
    {
        var task = Task.Run(call, CancellationToken.None);
        Track(key, task);
        return task;
    }

    /// <summary>Tracks a call already started.</summary>
    public void Track(string key, Task<T> task)
    {
        _calls[key] = task;
        _ = task.ContinueWith(
            t => _calls.TryRemove(new KeyValuePair<string, Task<T>>(key, t)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
