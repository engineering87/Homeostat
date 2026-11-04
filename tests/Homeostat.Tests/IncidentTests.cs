using System.Net;
using System.Text.Json;
using Homeostat.Healing;
using Homeostat.Http;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Tests;

public class IncidentTests
{
    private static Task<HomeostatTestHost> StartAsync(Action<HomeostatBuilder> notifier, string clientName, RecordingHandler handler, bool heals = false)
        => HomeostatTestHost.StartAsync(
            h =>
            {
                h.Configure(o => o.ApplicationName = "weather-api");
                h.AddCondition("feed-down", c => c.ForSource("feed").Heal(p =>
                {
                    p.With("renew", _ => { });
                    if (heals)
                    {
                        p.VerifyWith<EngineTests.AlwaysHealedVerifier>();
                    }
                    else
                    {
                        p.VerifyWith<CachingTests.NeverHealed>();
                    }
                }));
                notifier(h);
            },
            services => services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => handler));

    [Fact]
    public async Task PagerDuty_incident_is_triggered_when_healing_gives_up_and_resolved_when_cleared()
    {
        var handler = new RecordingHandler();
        await using var host = await StartAsync(
            h => h.AddPagerDutyIncidents(o => { o.RoutingKey = "R0UTINGKEY0000000000000000000000"; o.Component = "forecast"; }),
            "Homeostat.PagerDuty", handler);

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");
        Assert.Equal(HealingRunOutcome.NotHealed, run.Outcome);
        host.Publish("feed", SignalSeverity.Healthy);

        var calls = await handler.WaitForAsync(2);
        var trigger = calls[0].Body;
        Assert.Equal(new Uri("https://events.pagerduty.com/v2/enqueue"), calls[0].Uri);
        Assert.Equal("trigger", trigger.GetProperty("event_action").GetString());
        Assert.Equal("R0UTINGKEY0000000000000000000000", trigger.GetProperty("routing_key").GetString());
        Assert.Equal("homeostat.weather-api.feed-down", trigger.GetProperty("dedup_key").GetString());
        var payload = trigger.GetProperty("payload");
        Assert.Equal("error", payload.GetProperty("severity").GetString());
        Assert.Equal("weather-api", payload.GetProperty("source").GetString());
        Assert.Equal("forecast", payload.GetProperty("component").GetString());
        Assert.False(payload.TryGetProperty("group", out _));      // nulls are left out
        Assert.Equal("NotHealed", payload.GetProperty("custom_details").GetProperty("outcome").GetString());

        Assert.Equal("resolve", calls[1].Body.GetProperty("event_action").GetString());
        Assert.Equal("homeostat.weather-api.feed-down", calls[1].Body.GetProperty("dedup_key").GetString());
    }

    [Fact]
    public async Task Healed_runs_do_not_page_anyone()
    {
        var handler = new RecordingHandler();
        await using var host = await StartAsync(
            h => h.AddPagerDutyIncidents(o => { o.RoutingKey = "key"; o.ResolveWhenCleared = false; }),
            "Homeostat.PagerDuty", handler, heals: true);

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("feed-down");
        host.Publish("feed", SignalSeverity.Healthy);
        await Task.Delay(200);

        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Opsgenie_alert_is_created_and_closed_by_alias_on_Jira_Service_Management_by_default()
    {
        var handler = new RecordingHandler();
        await using var host = await StartAsync(
            h => h.AddOpsgenieIncidents(o => { o.ApiKey = "api-key"; o.Tags.Add("weather"); }),
            "Homeostat.Opsgenie", handler);

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("feed-down");
        host.Publish("feed", SignalSeverity.Healthy);

        var calls = await handler.WaitForAsync(2);
        Assert.Equal(new Uri("https://api.atlassian.com/jsm/ops/integration/v2/alerts"), calls[0].Uri);
        Assert.Equal("GenieKey api-key", calls[0].Authorization);
        var alert = calls[0].Body;
        Assert.Equal("homeostat.weather-api.feed-down", alert.GetProperty("alias").GetString());
        Assert.Equal("P2", alert.GetProperty("priority").GetString());
        Assert.True(alert.GetProperty("message").GetString()!.Length <= 130);
        Assert.Equal(["homeostat", "weather"], alert.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).Order());

        Assert.Equal(
            "https://api.atlassian.com/jsm/ops/integration/v2/alerts/homeostat.weather-api.feed-down/close?identifierType=alias",
            calls[1].Uri!.AbsoluteUri);
    }

    [Fact]
    public async Task Client_errors_are_not_retried()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.BadRequest };
        await using var host = await StartAsync(h => h.AddPagerDutyIncidents(o => o.RoutingKey = "key"), "Homeostat.PagerDuty", handler);

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("feed-down");
        await handler.WaitForAsync(1);
        await Task.Delay(300);

        Assert.Single(handler.Calls);
    }

    [Fact]
    public void Slack_messages_escape_text_and_carry_approval_buttons()
    {
        var approval = new Notifications.HomeostatEvent(
            Notifications.HomeostatEventKind.ApprovalRequested, DateTimeOffset.UnixEpoch, "weather-api", "feed-down",
            "Step 'failover' of 'feed-down' waits for approval. <!channel> <https://evil.example|click>")
        {
            Step = "failover",
            ApprovalId = "a1b2",
        };

        var json = JsonDocument.Parse(SlackSink.Payload(approval, new SlackOptions
        {
            Url = new Uri("https://hooks.slack.com/services/x"),
            ApprovalButtons = true,
            DashboardUrl = new Uri("https://weather-api.internal/homeostat/dashboard"),
        }).ToJsonString(HttpEventSink.Json)).RootElement;

        var section = json.GetProperty("blocks")[0].GetProperty("text").GetProperty("text").GetString()!;
        Assert.Contains("&lt;!channel&gt;", section);
        Assert.False(section.Contains("<!channel>", StringComparison.Ordinal));
        var buttons = json.GetProperty("blocks")[2].GetProperty("elements");
        Assert.Equal("homeostat_approve", buttons[0].GetProperty("action_id").GetString());
        Assert.Equal("a1b2", buttons[0].GetProperty("value").GetString());
        Assert.Equal("homeostat_reject", buttons[1].GetProperty("action_id").GetString());
        Assert.Equal("https://weather-api.internal/homeostat/dashboard", buttons[2].GetProperty("url").GetString());
    }

    [Fact]
    public void Slack_text_stays_within_the_block_limit_without_splitting_entities()
    {
        var e = new Notifications.HomeostatEvent(
            Notifications.HomeostatEventKind.ConditionActivated, DateTimeOffset.UnixEpoch, "weather-api", "feed-down",
            string.Concat(Enumerable.Repeat("a > b & c < d ", 400)));

        var json = JsonDocument.Parse(SlackSink.Payload(e, new SlackOptions { Url = new Uri("https://hooks.slack.com/x") }).ToJsonString(HttpEventSink.Json)).RootElement;
        var section = json.GetProperty("blocks")[0].GetProperty("text").GetProperty("text").GetString()!;

        Assert.True(section.Length <= 3000);
        var lastAmp = section.LastIndexOf('&');
        Assert.True(section.IndexOf(';', lastAmp) > lastAmp);    // the last entity is complete
        Assert.False(json.GetProperty("text").GetString()!.Contains('<', StringComparison.Ordinal));   // fallback escaped too
    }

    [Fact]
    public void A_run_completion_arriving_after_the_clear_does_not_open_an_incident()
    {
        var tracker = new IncidentTracker();
        var options = new PagerDutyOptions { RoutingKey = "key" };
        Notifications.HomeostatEvent Event(Notifications.HomeostatEventKind kind, string? outcome = null)
            => new(kind, DateTimeOffset.UnixEpoch, "weather-api", "feed-down", "x") { Outcome = outcome };

        Assert.False(tracker.Decide(options, Event(Notifications.HomeostatEventKind.ConditionActivated)).IsTrigger);
        Assert.True(tracker.Decide(options, Event(Notifications.HomeostatEventKind.ConditionCleared)).IsResolve);
        Assert.False(tracker.Decide(options, Event(Notifications.HomeostatEventKind.RunCompleted, "NotHealed")).IsTrigger);

        Assert.False(tracker.Decide(options, Event(Notifications.HomeostatEventKind.ConditionActivated)).IsTrigger);
        Assert.True(tracker.Decide(options, Event(Notifications.HomeostatEventKind.RunCompleted, "NotHealed")).IsTrigger);
    }

    [Fact]
    public void Teams_cards_are_adaptive_cards_with_a_dashboard_link()
    {
        var e = new Notifications.HomeostatEvent(
            Notifications.HomeostatEventKind.RunCompleted, DateTimeOffset.UnixEpoch, "weather-api", "feed-down", "Run ended.")
        { Outcome = "Healed" };

        var json = JsonDocument.Parse(TeamsSink.Payload(e, new TeamsOptions
        {
            Url = new Uri("https://example.webhook.office.com/x"),
            DashboardUrl = new Uri("https://weather-api.internal/homeostat/dashboard"),
        }).ToJsonString(HttpEventSink.Json)).RootElement;

        var attachment = json.GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
        var card = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("Healed", card.GetProperty("body")[2].GetProperty("facts")[1].GetProperty("value").GetString());
        Assert.Equal("Action.OpenUrl", card.GetProperty("actions")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Notifier_settings_are_validated_at_startup()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => HomeostatTestHost.StartAsync(h => h.AddPagerDutyIncidents(_ => { })));
        await Assert.ThrowsAnyAsync<Exception>(() => HomeostatTestHost.StartAsync(h => h.AddOpsgenieIncidents(o => o.Url = new Uri("/relative", UriKind.Relative))));
        await Assert.ThrowsAnyAsync<Exception>(() => HomeostatTestHost.StartAsync(h => h.AddSlackNotifications(_ => { })));
        await Assert.ThrowsAnyAsync<Exception>(() => HomeostatTestHost.StartAsync(h => h.AddTeamsNotifications(o => o.MaxAttempts = 0)));
    }

    internal sealed record Call(Uri? Uri, string? Authorization, JsonElement Body);

    internal sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<Call> _calls = [];

        public HttpStatusCode Status { get; init; } = HttpStatusCode.Accepted;

        public IReadOnlyList<Call> Calls
        {
            get
            {
                lock (_gate)
                {
                    return _calls.ToArray();
                }
            }
        }

        public async Task<IReadOnlyList<Call>> WaitForAsync(int count)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (Calls.Count < count)
            {
                await Task.Delay(5, cts.Token);
            }

            return Calls;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (_gate)
            {
                _calls.Add(new Call(request.RequestUri, request.Headers.Authorization?.ToString()
                    ?? (request.Headers.TryGetValues("Authorization", out var values) ? values.Single() : null),
                    JsonDocument.Parse(json).RootElement.Clone()));
            }

            return new HttpResponseMessage(Status);
        }
    }
}
