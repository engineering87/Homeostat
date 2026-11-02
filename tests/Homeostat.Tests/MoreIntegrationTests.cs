using System.Net;
using System.Text;
using System.Text.Json;
using Homeostat.Artemis;
using Homeostat.Consumers;
using Homeostat.MongoDB;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using MongoDB.Driver.Core.Configuration;

namespace Homeostat.Tests;

public class MoreIntegrationTests
{
    // ---------------------------------------------------------------- Artemis

    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request, body));
            return answer(request, body);
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Artemis_queue_is_read_with_one_jolokia_post()
    {
        var handler = new FakeHandler((_, _) => Json("""{ "request": {}, "value": { "MessageCount": 1200, "ConsumerCount": 0 }, "status": 200 }"""));
        var client = new JolokiaClient(new SingleClientFactory(new HttpClient(handler)), new ArtemisOptions
        {
            ManagementUrl = new Uri("http://artemis:8161"),
            BrokerName = "0.0.0.0",
            UserName = "monitor",
            Password = "secret",
        });

        var values = await client.ReadAsync(JolokiaClient.QueueMBean("0.0.0.0", "telemetry-ingest", "anycast", "telemetry-ingest"), ["MessageCount", "ConsumerCount"], CancellationToken.None);

        Assert.Equal((1200d, 0d), (values["MessageCount"], values["ConsumerCount"]));
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "http://artemis:8161/console/jolokia/"), (request.Method, request.RequestUri!.ToString()));
        Assert.Equal("http://artemis:8161", request.Headers.GetValues("Origin").Single());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("read", json.GetProperty("type").GetString());
        Assert.Equal(
            "org.apache.activemq.artemis:broker=\"0.0.0.0\",component=addresses,address=\"telemetry-ingest\",subcomponent=queues,routing-type=\"anycast\",queue=\"telemetry-ingest\"",
            json.GetProperty("mbean").GetString());
    }

    [Fact]
    public void Jolokia_errors_and_odd_names_are_handled()
    {
        var missing = Assert.Throws<ArtemisManagementException>(() =>
            JolokiaClient.Parse("""{ "error_type": "javax.management.InstanceNotFoundException", "status": 404 }""", ["MessageCount"]));
        Assert.Contains("does not exist", missing.Message);
        Assert.Throws<ArtemisManagementException>(() => JolokiaClient.Parse("""{ "value": { "MessageCount": 1 }, "status": 200 }""", ["MessageCount", "ConsumerCount"]));
        Assert.Equal(7, JolokiaClient.Parse("""{ "value": 7, "status": 200 }""", ["MessageCount"])["MessageCount"]);
        Assert.Equal("\"a\\\"b\\*c\"", JolokiaClient.Quote("a\"b*c"));
        Assert.Throws<ArgumentException>(() => new ArtemisOptions { ManagementUrl = new Uri("http://artemis:8161") }.Validate());
        Assert.Throws<ArgumentException>(() => new ArtemisOptions { ManagementUrl = new Uri("http://admin:pw@artemis:8161"), BrokerName = "b" }.Validate());
        Assert.Equal("https://gw/artemis/console/jolokia/", JolokiaClient.Endpoint(new Uri("https://gw/artemis/"), "/console/jolokia").ToString());
        Assert.Equal("http://artemis:8161", JolokiaClient.OriginOf(new Uri("http://artemis:8161/console")));
    }

    [Fact]
    public async Task Artemis_queue_signals_flag_a_queue_without_consumers_and_watch_dead_letters()
    {
        var (services, detector) = ProbeTests.Build(h =>
        {
            var options = new ArtemisQueueSignalOptions { DeadLetterQueue = "DLQ.telemetry-ingest" };
            options.Messages.UnhealthyAbove = 1_000;
            HomeostatArtemisExtensions.AddQueueSignals(h, "telemetry-ingest", options, (_, queue, address, _, _) =>
                Task.FromResult<IReadOnlyDictionary<string, double>>(queue == "telemetry-ingest" && address == "telemetry-ingest"
                    ? new Dictionary<string, double> { ["MessageCount"] = 1_200, ["ConsumerCount"] = 0 }
                    : queue == "DLQ.telemetry-ingest" && address == "DLQ"
                        ? new Dictionary<string, double> { ["MessageCount"] = 3, ["ConsumerCount"] = 0 }
                        : throw new InvalidOperationException($"unexpected {address}/{queue}")));
        });
        await using var _ = services;
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);

        var bySource = signals.Signals.ToDictionary(s => s.Source);
        Assert.Equal(SignalSeverity.Unhealthy, bySource["artemis:telemetry-ingest:messages"].Severity);
        Assert.Equal(SignalSeverity.Unhealthy, bySource["artemis:telemetry-ingest:consumers"].Severity);
        Assert.Equal(3, bySource["artemis:telemetry-ingest:dead-letter"].Value);
        Assert.Equal("artemis", bySource["artemis:telemetry-ingest:messages"].Tags!["origin"]);
    }

    [Fact]
    public async Task A_pause_gate_holds_a_receive_loop_until_resumed()
    {
        var gate = new PauseGate("telemetry-ingest");
        await gate.WaitWhilePausedAsync();                                 // open

        await gate.PauseAsync(CancellationToken.None);
        var waiting = gate.WaitWhilePausedAsync();
        Assert.False(waiting.IsCompleted);
        Assert.True(gate.IsPaused);

        await gate.ResumeAsync(CancellationToken.None);
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.IsPaused);

        await gate.PauseAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var canceled = gate.WaitWhilePausedAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
    }

    // ---------------------------------------------------------------- MongoDB

    [Fact]
    public void Replication_lag_is_the_furthest_secondary_behind_the_primary()
    {
        var now = new DateTime(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);
        ReplicaMember[] members =
        [
            new("db-0:27017", "PRIMARY", now), new("db-1:27017", "SECONDARY", now.AddSeconds(-2)),
            new("db-2:27017", "RECOVERING", now.AddSeconds(-90)), new("db-3:27017", "SECONDARY", now.AddHours(-1)),
            new("arbiter:27017", "ARBITER", null),
        ];
        Assert.Equal(3600, ReplicationLag.Seconds(members));
        Assert.Equal(90, ReplicationLag.Seconds(members, new HashSet<string> { "db-3:27017" }));      // a delayed member, left out
        Assert.Equal(88, ReplicationLag.Seconds(members.Where(m => m.State != "PRIMARY"), new HashSet<string> { "db-3:27017" }));   // election
        Assert.Equal(0, ReplicationLag.Seconds([new("db-0:27017", "PRIMARY", now)]));      // single-node replica set
    }

    [Fact]
    public async Task Mongo_events_become_primary_and_pool_signals()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IMongoClient>(_ => throw new NotSupportedException());
        services.AddHomeostat(h => h.AddMongoSignals("archive"));
        await using var provider = services.BuildServiceProvider();
        var signals = new CollectingPublisher();
        var events = provider.GetRequiredService<MongoClientEvents>();
        var detector = provider.GetServices<Detectors.ISignalDetector>().OfType<MongoSignalDetector>().Single();

        await detector.DetectAsync(signals, CancellationToken.None);
        Assert.Empty(signals.Signals);                                     // client not subscribed to the events

        var settings = new MongoClientSettings();
        var previous = false;
        settings.ClusterConfigurator = _ => previous = true;
        settings.AddHomeostatSignals(provider, "archive");
        settings.ClusterConfigurator(new ClusterBuilder());
        Assert.True(previous);                                             // an existing configurator is kept

        var first = new MongoClientSettings();
        var second = new MongoClientSettings();
        first.AddHomeostatSignals(provider, "archive");
        second.AddHomeostatSignals(provider, "archive");
        Assert.Same(first.ClusterConfigurator, second.ClusterConfigurator);  // the driver can share one cluster

        var state = events.For("archive");
        await detector.DetectAsync(signals, CancellationToken.None);
        Assert.Empty(signals.Signals);                                     // nothing heard from a server yet
        Assert.True(state.ClusterChanged(clusterId: 1, writable: false, connectedServers: 2));
        Assert.False(state.ClusterChanged(clusterId: 1, writable: false, connectedServers: 2));
        state.CheckoutFailed(timedOut: true);
        state.CheckoutFailed(timedOut: false);

        await detector.DetectAsync(signals, CancellationToken.None);
        var primary = signals.Signals.Single(s => s.Source == "mongodb:archive:primary");
        var pool = signals.Signals.Single(s => s.Source == "mongodb:archive:pool");
        Assert.Equal((SignalSeverity.Unhealthy, 2d), (primary.Severity, primary.Value!.Value));
        Assert.Equal((SignalSeverity.Unhealthy, 1d), (pool.Severity, pool.Value!.Value));

        state.CheckoutFailed(timedOut: true);
        await detector.DetectAsync(signals, CancellationToken.None);       // within the interval: nothing new
        Assert.Single(signals.Signals, s => s.Source == "mongodb:archive:pool");

        Assert.True(state.ClusterChanged(clusterId: 1, writable: true, connectedServers: 3));
        Assert.False(state.ClusterChanged(clusterId: 2, writable: true, connectedServers: 3));       // a second cluster, also writable
        Assert.True(state.ClusterChanged(clusterId: 2, writable: false, connectedServers: 1));       // the worst wins
        Assert.Equal(4, state.ConnectedServers);
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHomeostat(h => h.AddMongoSignals("a").AddMongoSignals("a")));

        Assert.Throws<InvalidOperationException>(() => new MongoClientSettings().AddHomeostatSignals(new ServiceCollection().BuildServiceProvider()));
        Assert.Throws<InvalidOperationException>(() => new MongoClientSettings().AddHomeostatSignals(provider, "other"));     // no AddMongoSignals("other")
    }
}
