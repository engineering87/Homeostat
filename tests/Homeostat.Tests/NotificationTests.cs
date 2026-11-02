using System.Net;
using System.Text.Json;
using Homeostat.Http;
using Homeostat.Notifications;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Tests;

public class NotificationTests
{
    [Fact]
    public async Task Audit_log_records_events_as_json_lines()
    {
        var directory = Path.Combine(Path.GetTempPath(), "homeostat-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "audit-{date}.jsonl");

        await using (var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.ApplicationName = "weather-api")
            .AddCondition("feed-down", c => c
                .ForSource("feed")
                .Heal(p => p.With("renew", _ => { }).VerifyWith<EngineTests.AlwaysHealedVerifier>()))
            .AddAuditLog(path)))
        {
            host.Publish("feed", SignalSeverity.Unhealthy);
            await host.WaitForRunAsync("feed-down");
        }

        // Disposing the host drains the queue.
        var file = Assert.Single(Directory.GetFiles(directory));
        Assert.Equal("audit-2030-01-01.jsonl", Path.GetFileName(file));

        var events = File.ReadAllLines(file).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(
            new[] { "ConditionActivated", "StepCompleted", "RunCompleted" },
            events.Select(e => e.GetProperty("kind").GetString()!).ToArray());
        Assert.All(events, e => Assert.Equal("weather-api", e.GetProperty("application").GetString()));
        Assert.Equal("Healed", events[2].GetProperty("outcome").GetString());
        Assert.False(events[0].TryGetProperty("error", out _), "null fields are omitted");

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Audit_log_records_runs_canceled_by_the_shutdown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "homeostat-tests", Guid.NewGuid().ToString("N"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("feed-down", c => c
                .ForSource("feed")
                .Heal(p => p
                    .With("slow", async (_, ct) =>
                    {
                        started.TrySetResult();
                        await Task.Delay(Timeout.Infinite, ct);
                    })
                    .ActionTimeout(TimeSpan.FromHours(1))))
            .AddAuditLog(Path.Combine(directory, "audit.jsonl")));

        host.Publish("feed", SignalSeverity.Unhealthy);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await host.DisposeAsync();

        var kinds = File.ReadAllLines(Path.Combine(directory, "audit.jsonl"))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Select(e => (Kind: e.GetProperty("kind").GetString(), Outcome: e.TryGetProperty("outcome", out var o) ? o.GetString() : null))
            .ToList();
        Assert.Contains(("RunCompleted", "Canceled"), kinds);

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Webhook_posts_selected_events_with_custom_payload_and_headers()
    {
        var handler = new CapturingHandler();
        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .AddCondition("feed-down", c => c
                    .ForSource("feed")
                    .Heal(p => p.With("renew", _ => { }).VerifyWith<EngineTests.AlwaysHealedVerifier>()))
                .AddWebhookNotifications(o =>
                {
                    o.Url = new Uri("https://hooks.example/homeostat");
                    o.Headers["X-Token"] = "secret";
                    o.CreatePayload = e => new { text = e.Summary, kind = e.Kind };
                }),
            services => services
                .AddHttpClient(WebhookOptions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler));

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("feed-down");

        var bodies = await handler.WaitForAsync(2);
        Assert.Equal(new[] { "ConditionActivated", "RunCompleted" }, bodies.Select(b => b.GetProperty("kind").GetString()!).ToArray());
        Assert.Equal("Condition 'feed-down' is active.", bodies[0].GetProperty("text").GetString());
        Assert.Equal("secret", handler.Token);
        Assert.Equal(new Uri("https://hooks.example/homeostat"), handler.Uri);
    }

    [Fact]
    public async Task Webhook_retries_failed_deliveries()
    {
        var handler = new CapturingHandler { FailuresBeforeSuccess = 2 };
        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .AddCondition("feed-down", c => c.ForSource("feed"))
                .AddWebhookNotifications(o => o.Url = new Uri("https://hooks.example/homeostat")),
            services => services
                .AddHttpClient(WebhookOptions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => handler));

        host.Publish("feed", SignalSeverity.Unhealthy);

        var body = Assert.Single(await handler.WaitForAsync(1));
        Assert.Equal("ConditionActivated", body.GetProperty("kind").GetString());
        Assert.Equal(3, handler.Attempts);
    }

    [Fact]
    public async Task Webhook_requires_an_absolute_url()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => HomeostatTestHost.StartAsync(h => h
            .AddCondition("feed-down", c => c.ForSource("feed"))
            .AddWebhookNotifications(_ => { })));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<JsonElement> _bodies = [];
        private int _attempts;

        public int FailuresBeforeSuccess { get; init; }

        public int Attempts => Volatile.Read(ref _attempts);

        public string? Token { get; private set; }

        public Uri? Uri { get; private set; }

        public async Task<IReadOnlyList<JsonElement>> WaitForAsync(int count)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!cts.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (_bodies.Count >= count)
                    {
                        return _bodies.ToArray();
                    }
                }

                await Task.Delay(5);
            }

            throw new TimeoutException($"Expected {count} webhook calls.");
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) <= FailuresBeforeSuccess)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                _bodies.Add(JsonDocument.Parse(json).RootElement.Clone());
                Token = request.Headers.TryGetValues("X-Token", out var values) ? values.Single() : null;
                Uri = request.RequestUri;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
