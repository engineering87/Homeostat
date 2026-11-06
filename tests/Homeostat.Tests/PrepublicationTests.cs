using System.Diagnostics.Metrics;
using Homeostat.Consumers;
using Homeostat.Detectors;
using Homeostat.Kafka;
using Homeostat.Metrics;
using Homeostat.Persistence;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Homeostat.Tests;

/// <summary>Additions made before the first public release: pause gates in DI, Npgsql pools, scaled metrics, store retention.</summary>
public class PrepublicationTests
{
    [Fact]
    public async Task A_registered_pause_gate_is_shared_and_follows_its_switch()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddPauseGate("telemetry-ingest")
            .AddCondition("archive-down", c => c.ForSource("archive-db").Heal(p => p
                .PauseConsumer("telemetry-ingest")
                .UseVerifier<BuiltInActionTests.AlwaysHealed>())));
        var gate = host.Services.GetRequiredKeyedService<PauseGate>("telemetry-ingest");
        Assert.Same(gate, host.Services.GetRequiredKeyedService<PauseGate>("telemetry-ingest"));

        host.Publish("archive-db", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("archive-down");
        await PollyControlTests.WaitUntilAsync(() => gate.IsPaused);

        host.Publish("archive-db", SignalSeverity.Healthy);
        await PollyControlTests.WaitUntilAsync(() => !gate.IsPaused);
    }

    [Fact]
    public async Task A_registered_kafka_gate_records_the_pause_for_the_consume_loop()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddKafkaPauseGate("telemetry")
            .AddCondition("archive-down", c => c.ForSource("archive-db")));
        var gate = host.Services.GetRequiredKeyedService<KafkaPauseGate>("telemetry");

        host.Services.GetRequiredService<IHomeostatSwitches>().TurnOn(ConsumerSwitches.Paused("telemetry"));
        await PollyControlTests.WaitUntilAsync(() => gate.IsPauseRequested);
        Assert.Contains("telemetry", host.Services.GetRequiredService<IHomeostatConsumers>().Names);
    }

    [Fact]
    public async Task The_npgsql_preset_reports_the_share_of_used_connections_of_one_pool()
    {
        using var meter = new Meter("Npgsql");
        meter.CreateObservableUpDownCounter("db.client.connection.count", () => new[]
        {
            new Measurement<int>(47, new("db.client.connection.pool.name", "reports"), new("db.client.connection.state", "used")),
            new Measurement<int>(3, new("db.client.connection.pool.name", "reports"), new("db.client.connection.state", "idle")),
            new Measurement<int>(90, new("db.client.connection.pool.name", "other"), new("db.client.connection.state", "used")),
        });

        var services = new ServiceCollection();
        var time = new FakeTimeProvider();
        services.AddSingleton<TimeProvider>(time);
        services.AddHomeostat(h => h.AddNpgsqlPoolSignal(o => { o.MaxPoolSize = 50; o.DataSourceName = "reports"; }));
        using var detector = services.BuildServiceProvider().GetServices<ISignalDetector>().OfType<MetricSignalDetector>().Single();

        var sink = new CollectingPublisher();
        await detector.DetectAsync(sink, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        await detector.DetectAsync(sink, CancellationToken.None);

        var signal = sink.Signals.Last(s => s.Source == "npgsql:pool");
        Assert.Equal(0.94, signal.Value!.Value, 6);
        Assert.Equal(SignalSeverity.Degraded, signal.Severity);
    }

    [Fact]
    public void Scale_rejects_zero_and_non_finite_factors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddHomeostat(h => h.AddMetricSignal("x", m => m.Scale(0).UnhealthyAbove(1))));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddHomeostat(h => h.AddMetricSignal("x", m => m.Scale(double.NaN).UnhealthyAbove(1))));
    }

    [Fact]
    public async Task The_file_store_forgets_idle_plans_after_the_configured_retention()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homeostat-{Guid.NewGuid():N}.json");
        try
        {
            using (var store = new FileHomeostatStore(path))
            {
                await store.UpdatePlanStateAsync("old", new PlanStateUpdate { LastRunEndedAt = DateTimeOffset.UtcNow.AddDays(-10) });
                await store.UpdatePlanStateAsync("recent", new PlanStateUpdate { LastRunEndedAt = DateTimeOffset.UtcNow.AddDays(-1) });
            }

            using (var withDefault = new FileHomeostatStore(path))
            {
                Assert.NotNull((await withDefault.GetPlanStateAsync("old")).LastRunEndedAt);
            }

            using (var withWeek = new FileHomeostatStore(path, new FileStoreOptions { PlanStateRetention = TimeSpan.FromDays(7) }))
            {
                Assert.Null((await withWeek.GetPlanStateAsync("old")).LastRunEndedAt);
                Assert.NotNull((await withWeek.GetPlanStateAsync("recent")).LastRunEndedAt);
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => new FileHomeostatStore(path, new FileStoreOptions { PlanStateRetention = TimeSpan.Zero }));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    [Theory]
    [InlineData("application/grpc", "14", true)]
    [InlineData("application/grpc+proto", "4", true)]
    [InlineData("application/grpc", "0", false)]
    [InlineData("application/grpc", "5", false)]
    [InlineData("application/json", "14", false)]
    public void Grpc_failures_hidden_behind_http_200_are_counted(string contentType, string status, bool failure)
    {
        using var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        response.Headers.TryAddWithoutValidation("grpc-status", status);

        Assert.Equal(failure, Homeostat.Http.HttpClientSignalOptions.IsGrpcFailure(response));
        Assert.Equal(failure, new Homeostat.Http.HttpClientSignalOptions().IsFailure(response));
    }
}
