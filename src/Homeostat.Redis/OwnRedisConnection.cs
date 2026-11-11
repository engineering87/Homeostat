using StackExchange.Redis;

namespace Homeostat.Redis;

/// <summary>
/// The client that <c>UseRedis(connectionString)</c> creates for the store and the coordinator, kept apart from any
/// <see cref="IConnectionMultiplexer"/> the application registers. Created on first use; a connection that throws is
/// not kept, so the next use tries again (<see cref="RedisConnection"/> spaces the attempts).
/// </summary>
internal sealed class OwnRedisConnection(string connectionString) : IDisposable
{
    private readonly object _gate = new();
    private IConnectionMultiplexer? _connection;
    private bool _disposed;

    public IConnectionMultiplexer Get()
    {
        if (Volatile.Read(ref _connection) is { } connection)
        {
            return connection;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is null)
            {
                // AbortOnConnectFail = false: the application starts even when Redis does not; the store fails closed
                // and the client keeps reconnecting in the background.
                var configuration = ConfigurationOptions.Parse(connectionString);
                configuration.AbortOnConnectFail = false;
                Volatile.Write(ref _connection, ConnectionMultiplexer.Connect(configuration));
            }

            return _connection!;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            // Also registered as the application's IConnectionMultiplexer when it has none: the container disposes it
            // again, which the client ignores.
            _disposed = true;
            _connection?.Dispose();
        }
    }
}
