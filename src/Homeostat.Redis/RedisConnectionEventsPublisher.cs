using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Homeostat.Redis;

/// <summary>
/// Turns the connection events of a Redis client into signals. It subscribes the first time the ping probe obtains
/// the client, so a Redis server that is down at startup neither blocks startup nor loses the events for good.
/// </summary>
internal sealed class RedisConnectionEventsPublisher(string source, SignalSeverity failureSeverity)
{
    private readonly object _gate = new();
    private IConnectionMultiplexer? _connection;
    private ISignalPublisher? _signals;
    private TimeProvider _timeProvider = TimeProvider.System;

    public void EnsureSubscribed(IConnectionMultiplexer connection, IServiceProvider services)
    {
        if (ReferenceEquals(Volatile.Read(ref _connection), connection))
        {
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_connection, connection))
            {
                return;
            }

            if (_connection is { } previous)
            {
                previous.ConnectionFailed -= OnConnectionFailed;
                previous.ConnectionRestored -= OnConnectionRestored;
            }

            _signals = services.GetRequiredService<ISignalPublisher>();
            _timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
            connection.ConnectionFailed += OnConnectionFailed;
            connection.ConnectionRestored += OnConnectionRestored;
            Volatile.Write(ref _connection, connection);
        }
    }

    private void OnConnectionFailed(object? sender, ConnectionFailedEventArgs e)
        => Publish(failureSeverity, "connection-failed", e, $"Redis connection to {e.EndPoint} failed ({e.FailureType}).");

    private void OnConnectionRestored(object? sender, ConnectionFailedEventArgs e)
    {
        // A restored connection to one endpoint does not mean the client is healthy again when other endpoints are
        // still down: publish Healthy only when the client reports itself connected.
        var severity = Volatile.Read(ref _connection)?.IsConnected == true ? SignalSeverity.Healthy : SignalSeverity.Degraded;
        Publish(severity, "connection-restored", e, $"Redis connection to {e.EndPoint} restored.");
    }

    private void Publish(SignalSeverity severity, string outcome, ConnectionFailedEventArgs e, string message)
    {
        _signals?.TryPublish(new Signal(source, severity, _timeProvider.GetUtcNow())
        {
            Message = message,
            Tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["origin"] = "redis-connection",
                ["outcome"] = outcome,
                ["failureType"] = e.FailureType.ToString(),
                ["connectionType"] = e.ConnectionType.ToString(),
            },
        });
    }
}

/// <summary>Fails startup when <c>AddRedisSignals</c> is used without a registered Redis client.</summary>
internal sealed class RedisSignalsValidator(IServiceProvider services) : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService<IServiceProviderIsService>()?.IsService(typeof(IConnectionMultiplexer)) == false)
        {
            throw new InvalidOperationException(
                "AddRedisSignals needs an IConnectionMultiplexer registered in the container, or UseRedis(connectionString).");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
