using System.Net;
using System.Reflection;
using System.Text.Json;
using Homeostat.Cli;
using Homeostat.Healing;
using Homeostat.Http;
using Homeostat.Persistence;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Homeostat.Tests;

/// <summary>Healing groups in the hosting packages: status, control endpoints, dashboard, chat, CLI and health check.</summary>
public class HealingGroupHostingTests
{
    // ---------------------------------------------------------------- status

    [Fact]
    public async Task Status_shows_the_groups_with_their_budget_and_the_group_of_conditions_runs_and_approvals()
    {
        var (app, client) = await StartWebAsync(h => h
            .ConfigureGroup("reports", g => { g.MaxActionsPerWindow = 2; g.MaxConcurrentHealings = 1; })
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")
                .Heal(p => p.With("restart", _ => { }).UseVerifier<BuiltInActionTests.AlwaysHealed>()))
            .AddCondition("reports-export", c => c.ForSource("export").InGroup("reports")
                .Heal(p => p.With("retry", _ => { }).RequireApproval(TimeSpan.FromMinutes(5))))
            .AddCondition("feed-down", c => c.ForSource("feed")));
        await using var _ = app;
        using var __ = client;
        var signals = app.Services.GetRequiredService<ISignalPublisher>();
        var control = app.Services.GetRequiredService<IHomeostatControl>();
        var approvals = app.Services.GetRequiredService<IHomeostatApprovals>();

        signals.TryPublish("reports", SignalSeverity.Unhealthy);
        signals.TryPublish("export", SignalSeverity.Unhealthy);
        await PollyControlTests.WaitUntilAsync(() => approvals.GetPending().Count == 1);
        await PollyControlTests.WaitUntilAsync(() => control.GetRecentRunsAsync().AsTask().Result.Count == 1);
        await control.PauseGroupAsync("ingest", by: "on-call");

        var status = await GetStatusAsync(client);

        var groups = status.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(["ingest", "reports"], groups.Select(g => g.GetProperty("name").GetString()));
        var reports = groups[1];
        Assert.False(reports.GetProperty("paused").GetBoolean());
        Assert.Equal(["reports-slow", "reports-export"], reports.GetProperty("conditions").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(1, reports.GetProperty("maxConcurrentHealings").GetInt32());
        Assert.Equal(2, reports.GetProperty("maxActionsPerWindow").GetInt32());
        Assert.Equal(1, reports.GetProperty("budget").GetProperty("used").GetInt32());
        Assert.Equal(2, reports.GetProperty("budget").GetProperty("limit").GetInt32());
        Assert.Equal(1, reports.GetProperty("budget").GetProperty("remaining").GetInt32());
        Assert.False(reports.TryGetProperty("pauseReason", out var _));

        // A group only paused: its pause shown, no conditions, no limits nor budget (left out, not null).
        var ingest = groups[0];
        Assert.True(ingest.GetProperty("paused").GetBoolean());
        Assert.Equal("on-call", ingest.GetProperty("pausedBy").GetString());
        Assert.Equal("paused by on-call", ingest.GetProperty("pauseReason").GetString());
        Assert.Empty(ingest.GetProperty("conditions").EnumerateArray());
        Assert.False(ingest.TryGetProperty("budget", out var _));
        Assert.False(ingest.TryGetProperty("pausedUntil", out var _));
        Assert.False(ingest.TryGetProperty("maxConcurrentHealings", out var _));
        Assert.False(status.GetProperty("paused").GetBoolean());

        var conditions = status.GetProperty("conditions").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        Assert.Equal("reports", conditions["reports-slow"].GetProperty("group").GetString());
        Assert.False(conditions["feed-down"].TryGetProperty("group", out var _));
        Assert.Equal("reports", status.GetProperty("recentRuns")[0].GetProperty("group").GetString());
        Assert.Equal("reports", status.GetProperty("pendingApprovals")[0].GetProperty("group").GetString());
    }

    [Fact]
    public async Task Without_groups_the_status_has_no_group_fields()
    {
        var (app, client) = await StartWebAsync(h => h
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p.With("renew", _ => { }).UseVerifier<BuiltInActionTests.AlwaysHealed>())));
        await using var _ = app;
        using var __ = client;
        app.Services.GetRequiredService<ISignalPublisher>().TryPublish("feed", SignalSeverity.Unhealthy);
        var control = app.Services.GetRequiredService<IHomeostatControl>();
        await PollyControlTests.WaitUntilAsync(() => control.GetRecentRunsAsync().AsTask().Result.Count == 1);

        var status = await GetStatusAsync(client);

        Assert.False(status.TryGetProperty("groups", out var _));
        Assert.False(status.GetProperty("conditions")[0].TryGetProperty("group", out var _));
        Assert.False(status.GetProperty("recentRuns")[0].TryGetProperty("group", out var _));
    }

    [Fact]
    public async Task A_group_budget_the_store_cannot_read_is_left_out_and_the_store_error_reported()
    {
        var store = GroupStore.Create();
        var (app, client) = await StartWebAsync(
            h => h.ConfigureGroup("reports", g => g.MaxActionsPerWindow = 2).AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")),
            store);
        await using var _ = app;
        using var __ = client;

        GroupStore.Of(store).FailGroupBudget = new InvalidOperationException("connection refused by redis-01.internal");
        var status = await GetStatusAsync(client);

        var reports = status.GetProperty("groups")[0];
        Assert.False(reports.TryGetProperty("budget", out var _));
        Assert.Equal(2, reports.GetProperty("maxActionsPerWindow").GetInt32());
        var error = status.GetProperty("storeError").GetString()!;
        Assert.Contains(nameof(InvalidOperationException), error);
        Assert.False(error.Contains("redis-01", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Object, status.GetProperty("budget").ValueKind);   // the global budget is still read
    }

    // ---------------------------------------------------------------- control endpoints

    [Fact]
    public async Task Group_pause_and_resume_endpoints_pause_and_resume_one_group()
    {
        var (app, client) = await StartWebAsync(h => h
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports/eu"))
            .AddCondition("ingest-lag", c => c.ForSource("ingest").InGroup("ingest")));
        await using var _ = app;
        using var __ = client;
        var control = app.Services.GetRequiredService<IHomeostatControl>();

        // Route values are decoded as switch names are: only %2F, so a name with a slash reaches its group.
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, $"/homeostat/groups/{Uri.EscapeDataString("reports/eu")}/pause?for=00:30:00")).Status);
        Assert.True(control.IsGroupPaused("reports/eu"));
        Assert.False(control.IsGroupPaused("ingest"));
        Assert.False(control.IsPaused);
        var pause = control.GetGroups().Single(g => g.Name == "reports/eu").Pause!;
        Assert.NotNull(pause.Until);

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, "/homeostat/groups/reports%2feu/resume")).Status);
        Assert.False(control.IsGroupPaused("reports/eu"));

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, "/homeostat/groups/ingest/pause")).Status);
        Assert.Null(control.GetGroups().Single(g => g.Name == "ingest").Pause!.Until);

        // The same validation as the global pause and the switch names.
        var (status, body) = await PostAsync(client, "/homeostat/groups/ingest/pause?for=soon");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("must be a positive duration", body);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/homeostat/groups/ingest/pause?for=31.00:00:00")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/homeostat/groups/bad%01name/pause")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/homeostat/groups/bad%01name/resume")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, $"/homeostat/groups/{new string('x', 201)}/pause")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/homeostat/groups/%20/pause")).Status);
    }

    [Fact]
    public async Task Group_endpoints_require_the_policy_and_refuse_cross_site_requests()
    {
        var (app, client) = await StartWebAsync(h => h.AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")), policy: "operations");
        await using var _ = app;
        using var __ = client;

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        foreach (var route in new[] { "/homeostat/groups/{group}/pause", "/homeostat/groups/{group}/resume" })
        {
            var endpoint = endpoints.Single(e => e.RoutePattern.RawText == route);
            Assert.Equal("operations", endpoint.Metadata.GetMetadata<IAuthorizeData>()?.Policy);
            Assert.Equal("POST", Assert.Single(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!.HttpMethods));
        }

        // Cross-site request forgery: the same filter as the other control endpoints.
        var (anonymous, anonymousClient) = await StartWebAsync(h => h.AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")));
        await using var open = new Owned(anonymous, anonymousClient);
        var control = anonymous.Services.GetRequiredService<IHomeostatControl>();
        using var bare = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = anonymousClient.BaseAddress };
        using (var response = await bare.PostAsync("/homeostat/groups/reports/pause", null))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("X-Requested-With", await response.Content.ReadAsStringAsync());
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, "/homeostat/groups/reports/pause"))
        {
            request.Headers.Add("X-Requested-With", "tests");
            request.Headers.Add("Sec-Fetch-Site", "cross-site");
            using var response = await bare.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await control.PauseGroupAsync("reports");
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/homeostat/groups/reports/resume"))
        {
            request.Headers.Add("Sec-Fetch-Site", "same-site");
            request.Headers.Add("X-Requested-With", "tests");
            using var response = await bare.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.True(control.IsGroupPaused("reports"));
    }

    [Fact]
    public async Task A_group_resume_that_times_out_answers_503_and_leaves_the_group_paused()
    {
        var store = GroupStore.Create();
        var (app, client) = await StartWebAsync(h => h.AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")), store);
        await using var _ = app;
        using var __ = client;
        var control = app.Services.GetRequiredService<IHomeostatControl>();

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, "/homeostat/groups/reports/pause")).Status);

        GroupStore.Of(store).FailGroupResume = new TimeoutException("redis-01.internal:6379 timed out");
        var (status, body) = await PostAsync(client, "/homeostat/groups/reports/resume");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("was not made", body);
        Assert.Contains("stays paused", body);
        Assert.False(body.Contains("redis-01", StringComparison.Ordinal));
        Assert.True(control.IsGroupPaused("reports"));

        // A store that refuses (not a timeout): nothing was changed.
        GroupStore.Of(store).FailGroupResume = new InvalidOperationException("connection refused by redis-01.internal");
        (status, body) = await PostAsync(client, "/homeostat/groups/reports/resume");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("nothing was changed", body);
        Assert.True(control.IsGroupPaused("reports"));
    }

    [Fact]
    public async Task A_group_pause_over_the_limit_answers_409_with_the_reason()
    {
        var (app, client) = await StartWebAsync(h => h.AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")));
        await using var _ = app;
        using var __ = client;
        var control = app.Services.GetRequiredService<IHomeostatControl>();
        for (var i = 0; i < ControlState.MaxGroupPauses; i++)
        {
            await control.PauseGroupAsync($"group-{i:000}");
        }

        var (status, body) = await PostAsync(client, "/homeostat/groups/reports/pause");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("At most 256 healing groups", body);
        Assert.False(control.IsGroupPaused("reports"));

        // Replacing a pause is always allowed.
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, "/homeostat/groups/group-000/pause")).Status);
    }

    // ---------------------------------------------------------------- dashboard

    [Fact]
    public async Task The_dashboard_shows_groups_with_pause_and_resume_buttons_rendered_as_text()
    {
        var js = await ReadAssetAsync("dashboard.js");
        var html = await ReadAssetAsync("dashboard.html");

        Assert.Contains("id=\"groups\"", html);
        Assert.Contains("id=\"group-pause-for\"", html);
        Assert.Contains("function renderGroups(groups", js);
        Assert.Contains("renderGroups(status.groups", js);
        Assert.Contains("post(`groups/${name}/resume`", js);
        Assert.Contains("`groups/${name}/pause?for=${encodeURIComponent(duration)}`", js);
        Assert.Contains("const name = encodeURIComponent(g.name);", js);
        Assert.Contains("c.group ?", js);
        Assert.Contains("a.group ?", js);
        Assert.Contains("r.group ?", js);
        Assert.Contains("\"X-Requested-With\": \"homeostat-dashboard\"", js);   // the group buttons go through post()
        Assert.False(js.Contains("innerHTML", StringComparison.Ordinal));
        Assert.False(js.Contains("insertAdjacentHTML", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- Slack and Teams

    [Fact]
    public void Slack_and_Teams_messages_mention_the_group_only_when_there_is_one()
    {
        var grouped = new Notifications.HomeostatEvent
        {
            Kind = Notifications.HomeostatEventKind.ApprovalRequested, Timestamp = DateTimeOffset.UnixEpoch, Application = "weather-api",
            Condition = "reports-slow", Group = "reports<eu>", Summary = "Approval needed.", Step = "restart", ApprovalId = "a1",
        };
        var plain = grouped with { Group = null, Condition = "feed-down" };
        var slack = new SlackOptions { Url = new Uri("https://hooks.slack.com/x"), ApprovalButtons = true };
        var teams = new TeamsOptions { Url = new Uri("https://example.webhook.office.com/x") };

        string SlackContext(Notifications.HomeostatEvent e) => JsonDocument.Parse(SlackSink.Payload(e, slack).ToJsonString(HttpEventSink.Json))
            .RootElement.GetProperty("blocks")[1].GetProperty("elements")[0].GetProperty("text").GetString()!;
        string SlackConfirm(Notifications.HomeostatEvent e) => JsonDocument.Parse(SlackSink.Payload(e, slack).ToJsonString(HttpEventSink.Json))
            .RootElement.GetProperty("blocks")[2].GetProperty("elements")[0].GetProperty("confirm").GetProperty("text").GetProperty("text").GetString()!;
        JsonElement TeamsFacts(Notifications.HomeostatEvent e) => JsonDocument.Parse(TeamsSink.Payload(e, teams).ToJsonString(HttpEventSink.Json))
            .RootElement.GetProperty("attachments")[0].GetProperty("content").GetProperty("body")[2].GetProperty("facts");

        Assert.Equal("Condition `reports-slow` in group `reports&lt;eu&gt;` · ApprovalRequested", SlackContext(grouped));
        Assert.Equal("Condition `feed-down` · ApprovalRequested", SlackContext(plain));
        Assert.Equal("reports-slow (group reports<eu>): restart", SlackConfirm(grouped));
        Assert.Equal("feed-down: restart", SlackConfirm(plain));

        var facts = TeamsFacts(grouped);
        Assert.Equal("Group", facts[1].GetProperty("title").GetString());
        Assert.Equal("reports\\<eu\\>", facts[1].GetProperty("value").GetString());
        Assert.DoesNotContain(TeamsFacts(plain).EnumerateArray(), f => f.GetProperty("title").GetString() == "Group");

        var signal = new Signal("reports", SignalSeverity.Unhealthy, DateTimeOffset.UnixEpoch);
        var pending = new PendingApproval
        {
            Id = "a1", ConditionName = "reports-slow", Group = "reports", Step = 0, StepName = "restart",
            RequestedAt = DateTimeOffset.UnixEpoch, ExpiresAt = DateTimeOffset.UnixEpoch.AddMinutes(5), TriggeringSignal = signal,
        };
        Assert.Contains("`reports-slow` in group `reports`, step `restart`", HomeostatTeamsEndpointExtensions.Pending([pending]));
        Assert.Contains("`feed-down`, step `restart`", HomeostatTeamsEndpointExtensions.Pending([pending with { Group = null, ConditionName = "feed-down" }]));
    }

    // ---------------------------------------------------------------- CLI

    [Fact]
    public async Task The_cli_pauses_and_resumes_a_group_and_prints_the_groups_in_the_status()
    {
        var (app, client) = await StartWebAsync(h => h
            .ConfigureGroup("reports", g => g.MaxActionsPerWindow = 3)
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")
                .Heal(p => p.With("restart", _ => { }).RequireApproval(TimeSpan.FromMinutes(5))))
            .AddCondition("feed-down", c => c.ForSource("feed")));
        await using var running = app;
        client.Dispose();
        var control = app.Services.GetRequiredService<IHomeostatControl>();
        var approvals = app.Services.GetRequiredService<IHomeostatApprovals>();

        var (code, output, error) = await RunCliAsync(app, "pause", "--group", "reports", "--for", "30m");
        Assert.True(code == 0, error);
        Assert.Equal("Group 'reports' paused for 30m.", output.Trim());
        Assert.True(control.IsGroupPaused("reports"));
        Assert.NotNull(control.GetGroups().Single().Pause!.Until);
        Assert.False(control.IsPaused);

        (code, output, _) = await RunCliAsync(app, "status");
        Assert.Equal(0, code);
        Assert.Contains("Groups:", output);
        Assert.Contains("  reports  paused    budget 0/3; paused until", output);
        Assert.Contains("homeostat pause --group <name>", output);
        Assert.Contains("(group reports)", output);

        (code, output, _) = await RunCliAsync(app, "resume", "-g", "reports");
        Assert.Equal(0, code);
        Assert.Equal("Group 'reports' resumed.", output.Trim());
        Assert.False(control.IsGroupPaused("reports"));

        app.Services.GetRequiredService<ISignalPublisher>().TryPublish("reports", SignalSeverity.Unhealthy);
        await PollyControlTests.WaitUntilAsync(() => approvals.GetPending().Count == 1);
        (_, output, _) = await RunCliAsync(app, "status");
        Assert.Contains("  reports  healing   budget 0/3", output);
        Assert.Contains($"{approvals.GetPending()[0].Id}  reports-slow: restart (group reports)", output);

        (code, _, error) = await RunCliAsync(app, "pause", "--group", "bad\u0001name");
        Assert.Equal(HomeostatCli.Failed, code);
        Assert.Contains("Rejected", error);
        Assert.False(control.IsGroupPaused("bad\u0001name"));
    }

    [Fact]
    public async Task The_cli_sends_group_names_escaped_and_shows_the_detail_of_an_error_answer()
    {
        var handler = new AnsweringHandler(HttpStatusCode.NoContent, string.Empty);
        var output = new StringWriter();
        var error = new StringWriter();

        Assert.Equal(0, await HomeostatCli.InvokeAsync(["pause", "--group", "reports/eu", "--for", "2h", "--url", "https://weather-api.internal"], output, error, handler));
        Assert.Equal(0, await HomeostatCli.InvokeAsync(["resume", "--group", "reports/eu", "--url", "https://weather-api.internal"], output, error, handler));
        Assert.Equal(
            ["https://weather-api.internal/homeostat/groups/reports%2Feu/pause?for=02%3A00%3A00", "https://weather-api.internal/homeostat/groups/reports%2Feu/resume"],
            handler.Requests.Select(u => u.AbsoluteUri));

        // The problem's detail is shown, and the exit code is unchanged (decision I7).
        var detail = "The Homeostat store did not answer in time; the change was not made: Homeostat (or the healing group) stays paused.";
        handler.Answer(HttpStatusCode.ServiceUnavailable, JsonSerializer.Serialize(new { title = "Service Unavailable", status = 503, detail }));
        Assert.Equal(HomeostatCli.Failed, await HomeostatCli.InvokeAsync(["resume", "--group", "reports", "--url", "https://weather-api.internal"], output, error, handler));
        Assert.Contains(detail, error.ToString());

        // A 404 with a problem detail shows it; a bare 404 keeps the message it had.
        handler.Answer(HttpStatusCode.NotFound, JsonSerializer.Serialize(new { title = "Not Found", status = 404, detail = "No healing group 'reprots'." }));
        error = new StringWriter();
        Assert.Equal(HomeostatCli.Failed, await HomeostatCli.InvokeAsync(["pause", "--group", "reprots", "--url", "https://weather-api.internal"], output, error, handler));
        Assert.Contains("Not found: No healing group 'reprots'.", error.ToString());
        handler.Answer(HttpStatusCode.NotFound, JsonSerializer.Serialize(new { title = "Not Found", status = 404 }));
        error = new StringWriter();
        Assert.Equal(HomeostatCli.Failed, await HomeostatCli.InvokeAsync(["pause", "--group", "reports", "--url", "https://weather-api.internal"], output, error, handler));
        Assert.Contains("the request may have been answered already", error.ToString());
    }

    // ---------------------------------------------------------------- health check

    [Fact]
    public async Task The_health_check_is_degraded_when_a_group_budget_is_exhausted_and_lists_paused_groups()
    {
        var store = new InMemoryHomeostatStore();
        await using var host = await HomeostatTestHost.StartAsync(
            h => h.UseStore(_ => store)
                .ConfigureGroup("reports", g => { g.MaxActionsPerWindow = 1; g.ActionBudgetWindow = TimeSpan.FromHours(1); })
                .ConfigureGroup("ingest", g => g.MaxActionsPerWindow = 5)
                .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")),
            services => services.AddHealthChecks().AddHomeostatCheck(failureStatus: HealthStatus.Unhealthy));
        var checks = host.Services.GetRequiredService<HealthCheckService>();

        var entry = (await checks.CheckHealthAsync()).Entries["homeostat"];
        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Empty((string[])entry.Data["paused_groups"]);
        Assert.Empty((string[])entry.Data["exhausted_group_budgets"]);

        Assert.True(await store.TryConsumeGroupBudgetAsync("reports", host.Time.GetUtcNow(), 1, TimeSpan.FromHours(1)));
        entry = (await checks.CheckHealthAsync()).Entries["homeostat"];
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);   // the attention status, like the global budget
        Assert.Contains("action budget of group 'reports' exhausted", entry.Description);
        Assert.Equal(["reports"], (string[])entry.Data["exhausted_group_budgets"]);

        await host.Control.PauseGroupAsync("ingest", by: "on-call");
        entry = (await checks.CheckHealthAsync()).Entries["homeostat"];
        Assert.Equal(["ingest"], (string[])entry.Data["paused_groups"]);
        Assert.Contains("paused groups: ingest (paused by on-call)", entry.Description);
        Assert.False((bool)entry.Data["paused"]);
    }

    [Fact]
    public async Task Without_groups_the_health_check_data_is_unchanged_and_a_failing_group_budget_read_is_a_store_outage()
    {
        await using (var plain = await HomeostatTestHost.StartAsync(
            h => h.AddCondition("feed-down", c => c.ForSource("feed")),
            services => services.AddHealthChecks().AddHomeostatCheck()))
        {
            var entry = (await plain.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync()).Entries["homeostat"];
            Assert.Equal(HealthStatus.Healthy, entry.Status);
            Assert.False(entry.Data.ContainsKey("paused_groups"));
            Assert.False(entry.Data.ContainsKey("exhausted_group_budgets"));
        }

        var store = GroupStore.Create();
        GroupStore.Of(store).FailGroupBudget = new InvalidOperationException("connection refused by redis-01.internal");
        await using var host = await HomeostatTestHost.StartAsync(
            h => h.UseStore(_ => store).ConfigureGroup("reports", g => g.MaxActionsPerWindow = 1),
            services => services.AddHealthChecks().AddHomeostatCheck(failureStatus: HealthStatus.Unhealthy));

        var failed = (await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync()).Entries["homeostat"];
        Assert.Equal(HealthStatus.Degraded, failed.Status);   // decision I6, whatever the failure status
        Assert.Equal("The Homeostat store is unavailable.", failed.Description);
        Assert.Null(failed.Exception);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<(WebApplication App, HttpClient Client)> StartWebAsync(
        Action<HomeostatBuilder> configure, IHomeostatStore? store = null, string? policy = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (policy is not null)
        {
            builder.Services.AddAuthorization(o => o.AddPolicy(policy, p => p.RequireAssertion(_ => true)));
        }

        builder.Services.AddHomeostat(h =>
        {
            h.Configure(o => { o.DryRun = false; o.ApplicationName = "weather-api"; });
            if (store is not null)
            {
                h.UseStore(_ => store);
            }

            configure(h);
        });
        var app = builder.Build();
        if (policy is null)
        {
            app.MapHomeostatStatus(allowAnonymous: true);
            app.MapHomeostatControl(allowAnonymous: true);
        }
        else
        {
            app.MapHomeostatStatus(policy);
            app.MapHomeostatControl(policy);
        }

        await app.StartAsync();
        var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Add("X-Requested-With", "tests");
        return (app, client);
    }

    private sealed class Owned(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private static async Task<JsonElement> GetStatusAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/homeostat/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(HttpClient client, string path)
    {
        using var response = await client.PostAsync(path, null);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<(int Code, string Output, string Error)> RunCliAsync(WebApplication app, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await HomeostatCli.InvokeAsync([.. args, "--url", app.Urls.First()], output, error, new HttpClientHandler { UseProxy = false });
        return (code, output.ToString(), error.ToString());
    }

    private static async Task<string> ReadAssetAsync(string name)
    {
        using var reader = new StreamReader(typeof(HomeostatDashboardExtensions).Assembly.GetManifestResourceStream($"Homeostat.AspNetCore.Dashboard.{name}")!);
        return await reader.ReadToEndAsync();
    }

    /// <summary>Answers every request alike and records its address.</summary>
    private sealed class AnsweringHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private (HttpStatusCode Status, string Body) _answer = (status, body);

        public List<Uri> Requests { get; } = [];

        public void Answer(HttpStatusCode code, string text) => _answer = (code, text);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("homeostat-cli", request.Headers.GetValues("X-Requested-With").Single());
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(_answer.Status)
            {
                RequestMessage = request,
                Content = new StringContent(_answer.Body, System.Text.Encoding.UTF8, "application/problem+json"),
            });
        }
    }

    /// <summary>The in-memory store, except that group resumes or group budget reads can be made to fail.</summary>
    public class GroupStore : DispatchProxy
    {
        private IHomeostatStore _inner = null!;

        /// <summary>When set, a group resume (a group pause write without a pause) fails with it.</summary>
        public volatile Exception? FailGroupResume;

        /// <summary>When set, reading the use of a group budget fails with it.</summary>
        public volatile Exception? FailGroupBudget;

        public static IHomeostatStore Create()
        {
            var proxy = Create<IHomeostatStore, GroupStore>();
            ((GroupStore)(object)proxy)._inner = new InMemoryHomeostatStore();
            return proxy;
        }

        public static GroupStore Of(IHomeostatStore store) => (GroupStore)(object)store;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IHomeostatStore.TrySetGroupPauseAsync) && args![1] is null && FailGroupResume is { } resume)
            {
                throw resume;
            }

            if (targetMethod.Name == nameof(IHomeostatStore.GetGroupBudgetUsedAsync) && FailGroupBudget is { } budget)
            {
                throw budget;
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }
}
