using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Homeostat.AspNetCore;
using Homeostat.Signals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Homeostat.Tests;

public class AlertIngestionTests
{
    private const string Token = "s3cret-token-for-tests-0123456789";

    private static async Task<(WebApplication App, HttpClient Client, CollectingPublisher Signals)> StartAsync(
        Action<AlertIngestionOptions>? configure = null,
        FakeTimeProvider? time = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        // Simulated time: alert state is republished only when a test advances it.
        builder.Services.AddSingleton<TimeProvider>(time ?? new FakeTimeProvider());
        builder.Services.AddHomeostat();
        var signals = new CollectingPublisher();
        builder.Services.AddSingleton<ISignalPublisher>(signals);

        var app = builder.Build();
        app.MapHomeostatAlerts(configure: o =>
        {
            o.Token = Token;
            configure?.Invoke(o);
        });
        await app.StartAsync();
        var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return (app, client, signals);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(HttpClient client, string path, string json)
    {
        using var response = await client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
        var text = await response.Content.ReadAsStringAsync();
        var body = text.StartsWith('{') ? JsonDocument.Parse(text).RootElement.Clone() : default;
        return (response.StatusCode, body);
    }

    [Fact]
    public async Task Alertmanager_alerts_become_signals()
    {
        var (app, client, signals) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        var (status, body) = await PostAsync(client, "/homeostat/alerts/alertmanager", """
            {
              "version": "4", "status": "firing", "receiver": "homeostat",
              "alerts": [
                { "status": "firing", "labels": { "alertname": "QueueBacklogHigh", "severity": "critical", "instance": "worker-1" },
                  "annotations": { "summary": "Backlog above 10k" }, "fingerprint": "abc123" },
                { "status": "firing", "labels": { "alertname": "DiskAlmostFull", "severity": "warning" }, "annotations": {} },
                { "status": "resolved", "labels": { "alertname": "CertificateExpiring", "severity": "critical" } },
                { "status": "firing", "labels": { "severity": "critical" } },
                "not an object"
              ]
            }
            """);

        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(3, body.GetProperty("accepted").GetInt32());
        Assert.Equal(2, body.GetProperty("skipped").GetInt32());

        var backlog = signals.Signals.Single(s => s.Source == "alert:QueueBacklogHigh");
        Assert.Equal(SignalSeverity.Unhealthy, backlog.Severity);
        Assert.Equal("Backlog above 10k", backlog.Message);
        Assert.Equal("worker-1", backlog.Tags!["instance"]);
        Assert.Equal("alertmanager", backlog.Tags["origin"]);
        Assert.Equal("abc123", backlog.Tags["fingerprint"]);
        Assert.Equal("firing", backlog.Tags["status"]);
        Assert.Equal(SignalSeverity.Degraded, signals.Signals.Single(s => s.Source == "alert:DiskAlmostFull").Severity);
        Assert.Equal(SignalSeverity.Healthy, signals.Signals.Single(s => s.Source == "alert:CertificateExpiring").Severity);
    }

    [Fact]
    public async Task Series_of_one_alert_are_aggregated_so_a_resolved_one_does_not_hide_a_firing_one()
    {
        var (app, client, signals) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        await PostAsync(client, "/homeostat/alerts/alertmanager", """
            { "alerts": [
              { "status": "firing", "labels": { "alertname": "HighLatency", "instance": "pod-a", "severity": "critical" }, "fingerprint": "a" },
              { "status": "resolved", "labels": { "alertname": "HighLatency", "instance": "pod-b", "severity": "critical" }, "fingerprint": "b" } ] }
            """);
        var first = signals.Signals.Single();
        Assert.Equal(SignalSeverity.Unhealthy, first.Severity);
        Assert.Equal("1", first.Tags!["firing"]);
        Assert.Equal("pod-a", first.Tags["instance"]);

        await PostAsync(client, "/homeostat/alerts/alertmanager", """
            { "alerts": [ { "status": "firing", "labels": { "alertname": "HighLatency", "instance": "pod-b", "severity": "warning" }, "fingerprint": "b" } ] }
            """);
        Assert.Equal(SignalSeverity.Unhealthy, signals.Signals[^1].Severity);    // pod-a is still the worst
        Assert.Equal("2", signals.Signals[^1].Tags!["firing"]);

        await PostAsync(client, "/homeostat/alerts/alertmanager", """
            { "alerts": [ { "status": "resolved", "labels": { "alertname": "HighLatency", "instance": "pod-a", "severity": "critical" }, "fingerprint": "a" } ] }
            """);
        Assert.Equal(SignalSeverity.Degraded, signals.Signals[^1].Severity);     // only pod-b, a warning

        await PostAsync(client, "/homeostat/alerts/alertmanager", """
            { "alerts": [ { "status": "resolved", "labels": { "alertname": "HighLatency", "instance": "pod-b" }, "fingerprint": "b" } ] }
            """);
        Assert.Equal(SignalSeverity.Healthy, signals.Signals[^1].Severity);
        Assert.Equal("0", signals.Signals[^1].Tags!["firing"]);
    }

    [Fact]
    public async Task Alert_state_is_republished_and_firing_alerts_expire()
    {
        var time = new FakeTimeProvider();
        var (app, client, signals) = await StartAsync(o => o.FiringTimeToLive = TimeSpan.FromMinutes(1), time);
        await using var _ = app;
        using var __ = client;

        await PostAsync(client, "/homeostat/alerts/azure-monitor", """
            { "data": { "essentials": { "alertId": "1", "alertRule": "log-errors", "severity": "Sev2", "monitorCondition": "Fired" } } }
            """);
        Assert.Single(signals.Signals);

        time.Advance(TimeSpan.FromSeconds(10));                 // detection interval: published again
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(3, signals.Signals.Count);
        Assert.All(signals.Signals, s => Assert.Equal(SignalSeverity.Degraded, s.Severity));

        time.Advance(TimeSpan.FromSeconds(50));                 // never resolved: expires after the time to live
        Assert.Equal(SignalSeverity.Healthy, signals.Signals[^1].Severity);

        time.Advance(TimeSpan.FromMinutes(2));                  // healthy long enough: no longer tracked
        var count = signals.Signals.Count;
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(count, signals.Signals.Count);
    }

    [Fact]
    public async Task Grafana_alerts_carry_their_single_value_and_use_the_prefix()
    {
        var (app, client, signals) = await StartAsync(o => { o.SourcePrefix = "grafana:"; o.SourceLabel = "rule"; });
        await using var _ = app;
        using var __ = client;

        var (status, _) = await PostAsync(client, "/homeostat/alerts/grafana", """
            { "receiver": "homeostat", "status": "firing", "alerts": [
              { "status": "firing", "labels": { "rule": "LatencyHigh" }, "values": { "B": 812.5 } },
              { "status": "firing", "labels": { "rule": "Ambiguous" }, "values": { "A": 1, "B": 2 } } ] }
            """);
        Assert.Equal(HttpStatusCode.Accepted, status);
        var latency = signals.Signals.Single(s => s.Source == "grafana:LatencyHigh");
        Assert.Equal(812.5, latency.Value);
        Assert.Equal("grafana", latency.Tags!["origin"]);
        Assert.Null(signals.Signals.Single(s => s.Source == "grafana:Ambiguous").Value);
    }

    [Fact]
    public async Task Azure_Monitor_common_alert_schema_becomes_a_signal()
    {
        var (app, client, signals) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        var fired = """
            { "schemaId": "azureMonitorCommonAlertSchema", "data": {
                "essentials": { "alertId": "/subscriptions/x/alerts/1", "alertRule": "api-5xx-rate", "severity": "Sev1",
                                "signalType": "Metric", "monitorCondition": "Fired", "monitoringService": "Platform",
                                "description": "5xx rate above 5%" },
                "alertContext": { "condition": { "allOf": [ { "metricName": "Http5xx", "metricValue": 7.25 } ] } } } }
            """;
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, "/homeostat/alerts/azure-monitor", fired)).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, "/homeostat/alerts/azure-monitor",
            fired.Replace("\"Fired\"", "\"Resolved\"", StringComparison.Ordinal))).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, "/homeostat/alerts/azure-monitor",
            fired.Replace("Sev1", "Sev3", StringComparison.Ordinal))).Status);

        Assert.Equal(
            [SignalSeverity.Unhealthy, SignalSeverity.Healthy, SignalSeverity.Degraded],
            signals.Signals.Select(s => s.Severity));
        var first = signals.Signals[0];
        Assert.Equal("alert:api-5xx-rate", first.Source);
        Assert.Equal(7.25, first.Value);
        Assert.Equal("5xx rate above 5%", first.Message);
        Assert.Equal("Metric", first.Tags!["signalType"]);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/homeostat/alerts/azure-monitor", "{\"data\":{}}")).Status);
    }

    [Fact]
    public async Task Generic_signals_endpoint_accepts_objects_and_arrays()
    {
        var (app, client, signals) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        var (status, body) = await PostAsync(client, "/homeostat/alerts/signals", """
            [ { "source": "batch-import", "severity": "unhealthy", "value": 3, "message": "3 failures", "tags": { "job": "nightly" } },
              { "source": "batch-import", "severity": "7" },
              { "source": "batch-import", "severity": "Healthy, Degraded" },
              { "source": "batch-import", "severity": "Broken" },
              { "severity": "Healthy" } ]
            """);
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(1, body.GetProperty("accepted").GetInt32());
        Assert.Equal(4, body.GetProperty("skipped").GetInt32());

        var signal = signals.Signals.Single();
        Assert.Equal(SignalSeverity.Unhealthy, signal.Severity);
        Assert.Equal(3, signal.Value);
        Assert.Equal("alert:batch-import", signal.Source);
        Assert.Equal("nightly", signal.Tags!["job"]);
        Assert.Equal("signal-endpoint", signal.Tags["origin"]);

        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, "/homeostat/alerts/signals", """{ "source": "x", "severity": "Healthy" }""")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/homeostat/alerts/signals", "{ not json")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/homeostat/alerts/signals", "42")).Status);
    }

    [Fact]
    public async Task Token_is_required_from_header_or_query()
    {
        var (app, client, signals) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        const string payload = """{ "source": "x", "severity": "Unhealthy" }""";

        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, "/homeostat/alerts/signals", payload)).Status);
        // The query string is accepted only where senders cannot set headers.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, $"/homeostat/alerts/signals?token={Token}", payload)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, "/homeostat/alerts/azure-monitor?token=wrong", "{}")).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, $"/homeostat/alerts/azure-monitor?token={Token}",
            """{ "data": { "essentials": { "alertRule": "x", "monitorCondition": "Fired" } } }""")).Status);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token-of-enough-length");
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, "/homeostat/alerts/signals", payload)).Status);
        Assert.Single(signals.Signals);
    }

    [Fact]
    public async Task Oversized_bodies_and_too_many_alerts_are_limited()
    {
        var (app, client, signals) = await StartAsync(o => { o.MaxRequestBodySize = 2048; o.MaxAlertsPerRequest = 2; });
        await using var _ = app;
        using var __ = client;

        var huge = $$"""{ "source": "x", "severity": "Unhealthy", "message": "{{new string('a', 4096)}}" }""";
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostAsync(client, "/homeostat/alerts/signals", huge)).Status);

        var (status, body) = await PostAsync(client, "/homeostat/alerts/signals",
            "[" + string.Join(",", Enumerable.Repeat("""{ "source": "x", "severity": "Degraded" }""", 3)) + "]");
        Assert.Equal(HttpStatusCode.Accepted, status);
        Assert.Equal(2, body.GetProperty("accepted").GetInt32());
        Assert.Equal(1, body.GetProperty("skipped").GetInt32());
        Assert.Equal(2, signals.Signals.Count);
    }

    [Fact]
    public void Alert_endpoints_refuse_to_map_without_protection()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddHomeostat();
        var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.MapHomeostatAlerts());
        Assert.Throws<ArgumentException>(() => app.MapHomeostatAlerts(configure: o => o.Token = "short"));
        Assert.Throws<ArgumentException>(() => app.MapHomeostatAlerts(configure: o => { o.Token = Token; o.DefaultSeverity = SignalSeverity.Healthy; }));
        Assert.Throws<ArgumentException>(() => app.MapHomeostatAlerts(configure: o => o.Token = "has white space inside it"));
        app.MapHomeostatAlerts(prefix: "/c", configure: o => o.Token = Token + "\n");   // a trailing newline from a secret file is trimmed
        app.MapHomeostatAlerts("operations", prefix: "/a");
        app.MapHomeostatAlerts(prefix: "/b", allowAnonymous: true);
    }

    [Fact]
    public async Task Alert_drives_a_condition_end_to_end()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var healed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Services.AddHomeostat(h => h
            .Configure(o => o.DryRun = false)
            .AddCondition("disk-almost-full", c => c.ForSource("alert:DiskAlmostFull").WhenSeverityAtLeast(SignalSeverity.Degraded).Heal(p => p
                .With("prune-temp-files", _ => healed.TrySetResult())
                .UseVerifier<BuiltInActionTests.AlwaysHealed>())));
        var app = builder.Build();
        app.MapHomeostatAlerts(allowAnonymous: true);
        await app.StartAsync();
        await using var _ = app;
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.First()) };

        var (status, _) = await PostAsync(client, "/homeostat/alerts/alertmanager",
            """{ "alerts": [ { "status": "firing", "labels": { "alertname": "DiskAlmostFull", "severity": "warning" } } ] }""");

        Assert.Equal(HttpStatusCode.Accepted, status);
        await healed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
