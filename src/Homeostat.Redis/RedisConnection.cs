using StackExchange.Redis;

namespace Homeostat.Redis;

/// <summary>
/// The Redis connection of the store or the coordinator, resolved on first use and kept once it succeeds. A resolution
/// that fails is not tried again for a few seconds: with <c>AbortOnConnectFail=true</c> and Redis unreachable,
/// resolving blocks a thread for the whole connect timeout, and doing it on every store call would hold one thread-pool
/// thread per call (the same back-off as the file store's).
/// </summary>
internal sealed class RedisConnection(Func<IConnectionMultiplexer> resolve)
{
    internal static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private IConnectionMultiplexer? _connection;
    private Exception? _failure;
    private long _nextAttempt;

    public IConnectionMultiplexer Get()
    {
        if (Volatile.Read(ref _connection) is { } connection)
        {
            return connection;
        }

        // One resolution at a time: callers that arrive meanwhile wait for it instead of each starting their own.
        lock (_gate)
        {
            if (_connection is not null)
            {
                return _connection;
            }

            var now = Environment.TickCount64;
            if (_failure is not null && now < _nextAttempt)
            {
                throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "The Redis connection could not be resolved; trying again shortly.", _failure);
            }

            try
            {
                var resolved = resolve();
                _failure = null;
                Volatile.Write(ref _connection, resolved);
                return resolved;
            }
            catch (Exception ex)
            {
                _failure = ex;
                _nextAttempt = Environment.TickCount64 + (long)RetryEvery.TotalMilliseconds;
                throw;
            }
        }
    }
}
