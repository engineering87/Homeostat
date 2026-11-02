using System.Collections.Concurrent;

namespace Homeostat.Coordination;

/// <summary>Default coordinator: leases are exclusive within the process only, and never lost.</summary>
public sealed class InMemoryCoordinator : IHomeostatCoordinator
{
    private readonly ConcurrentDictionary<string, object> _held = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<IHomeostatLease?> TryAcquireAsync(string resource, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        var token = new object();
        return ValueTask.FromResult<IHomeostatLease?>(_held.TryAdd(resource, token) ? new Lease(this, resource, token) : null);
    }

    private sealed class Lease(InMemoryCoordinator owner, string resource, object token) : IHomeostatLease
    {
        public CancellationToken Lost => CancellationToken.None;

        public ValueTask DisposeAsync()
        {
            owner._held.TryRemove(new KeyValuePair<string, object>(resource, token));
            return ValueTask.CompletedTask;
        }
    }
}
