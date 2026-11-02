using System.Net;
using System.Net.Sockets;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Homeostat.Tests;

public class RedisSignalTests
{
    [Fact]
    public async Task Redis_signals_publish_the_ping_round_trip()
    {
        using var redis = RedisFixture.Connect();
        var (services, detector) = ProbeTests.Build(h =>
        {
            h.Services.AddSingleton<IConnectionMultiplexer>(redis);
            h.AddRedisSignals("session-cache", o => { o.DegradedAbove = 5_000; o.PublishConnectionEvents = false; });
        });
        await using var _ = services;
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);

        var signal = signals.Signals.Single();
        Assert.Equal("session-cache", signal.Source);
        Assert.Equal(SignalSeverity.Healthy, signal.Severity);
        Assert.True(signal.Value >= 0);
        Assert.Equal("redis-ping", signal.Tags!["origin"]);
    }

    [Fact]
    public async Task Unreachable_redis_publishes_a_failure_and_startup_does_not_throw()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // Nothing listens on the port any more.

        using var redis = await ConnectionMultiplexer.ConnectAsync($"127.0.0.1:{port},abortConnect=false,connectTimeout=200");
        var (services, detector) = ProbeTests.Build(h =>
        {
            h.Services.AddSingleton<IConnectionMultiplexer>(redis);
            h.AddRedisSignals();
        });
        await using var _ = services;

        foreach (var hosted in services.GetServices<IHostedService>().Where(s => s.GetType().Name == "RedisSignalsValidator"))
        {
            await hosted.StartAsync(CancellationToken.None);
        }

        var signals = new CollectingPublisher();
        await detector.DetectAsync(signals, CancellationToken.None);

        var signal = signals.Signals.Single();
        Assert.Equal("redis", signal.Source);
        Assert.Equal(SignalSeverity.Unhealthy, signal.Severity);
        Assert.Null(signal.Value);
    }

    [Fact]
    public async Task Redis_signals_without_a_connection_fail_at_startup()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddHomeostat(h => h.AddRedisSignals());
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().Single(s => s.GetType().Name == "RedisSignalsValidator");

        await Assert.ThrowsAsync<InvalidOperationException>(() => hosted.StartAsync(CancellationToken.None));
    }
}
