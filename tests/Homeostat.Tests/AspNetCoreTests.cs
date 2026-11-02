using System.Net;
using System.Text.Json;
using Homeostat.Signals;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Homeostat.Tests;

public class AspNetCoreTests
{
    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(Action<HomeostatBuilder> configure, bool anonymousControl = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat(h =>
        {
            h.Configure(o =>
            {
                o.DryRun = false;
                o.ApplicationName = "weather-api";
            });
            configure(h);
        });

        var app = builder.Build();
        if (anonymousControl)
        {
            app.MapHomeostatStatus(allowAnonymous: true);
            app.MapHomeostatControl(allowAnonymous: true);
        }
        else
        {
            app.MapHomeostatStatus("operations");
            app.MapHomeostatControl("operations");
        }

        await app.StartAsync();
        var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Add("X-Requested-With", "tests");
        return (app, client);
    }

    private static async Task<JsonElement> GetStatusAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/homeostat/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<JsonElement> WaitForStatusAsync(HttpClient client, Func<JsonElement, bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var status = await GetStatusAsync(client);
            if (predicate(status))
            {
                return status;
            }

            await Task.Delay(20, cts.Token);
        }
    }

    [Fact]
    public async Task Status_endpoint_reports_conditions_switches_and_budget()
    {
        var (app, client) = await StartAsync(h => h.AddCondition("feed-down", c => c.ForSource("feed")));
        await using var _ = app;
        using var __ = client;

        var status = await GetStatusAsync(client);
        Assert.Equal("weather-api", status.GetProperty("application").GetString());
        Assert.False(status.GetProperty("paused").GetBoolean());
        Assert.False(status.GetProperty("dryRun").GetBoolean());
        Assert.Equal(10, status.GetProperty("budget").GetProperty("limit").GetInt32());
        var condition = Assert.Single(status.GetProperty("conditions").EnumerateArray().ToList());
        Assert.Equal("feed-down", condition.GetProperty("name").GetString());
        Assert.Equal("Inactive", condition.GetProperty("status").GetString());

        app.Services.GetRequiredService<ISignalPublisher>().TryPublish("feed", SignalSeverity.Unhealthy, "timeout");
        status = await WaitForStatusAsync(client, s => s.GetProperty("conditions")[0].GetProperty("status").GetString() == "Active");
        Assert.Equal("timeout", status.GetProperty("conditions")[0].GetProperty("lastMessage").GetString());
        Assert.Equal("Unhealthy", status.GetProperty("conditions")[0].GetProperty("lastSeverity").GetString());
    }

    [Fact]
    public async Task Control_endpoints_pause_resume_and_flip_switches()
    {
        var (app, client) = await StartAsync(h => h.AddCondition("feed-down", c => c.ForSource("feed")));
        await using var _ = app;
        using var __ = client;

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/homeostat/pause", null)).StatusCode);
        Assert.True((await GetStatusAsync(client)).GetProperty("paused").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/homeostat/resume", null)).StatusCode);
        Assert.False((await GetStatusAsync(client)).GetProperty("paused").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/homeostat/switches/serve-cached/on", null)).StatusCode);
        Assert.Equal("serve-cached", Assert.Single((await GetStatusAsync(client)).GetProperty("switches").EnumerateArray().ToList()).GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/homeostat/switches/serve-cached/off", null)).StatusCode);
        Assert.Empty((await GetStatusAsync(client)).GetProperty("switches").EnumerateArray().ToList());
    }

    [Fact]
    public async Task Approval_endpoints_answer_pending_requests()
    {
        var (app, client) = await StartAsync(h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p.With("renew", _ => { }).RequireApproval().VerifyWith<EngineTests.AlwaysHealedVerifier>())));
        await using var _ = app;
        using var __ = client;

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/homeostat/approvals/unknown/approve", null)).StatusCode);

        app.Services.GetRequiredService<ISignalPublisher>().TryPublish("feed", SignalSeverity.Unhealthy);
        var status = await WaitForStatusAsync(client, s => s.GetProperty("pendingApprovals").GetArrayLength() == 1);
        var id = status.GetProperty("pendingApprovals")[0].GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/homeostat/approvals/{id}/approve", null)).StatusCode);

        status = await WaitForStatusAsync(client, s => s.GetProperty("recentRuns").GetArrayLength() == 1);
        var run = status.GetProperty("recentRuns")[0];
        Assert.Equal("Healed", run.GetProperty("outcome").GetString());
        Assert.Equal("renew", run.GetProperty("steps")[0].GetProperty("name").GetString());
        Assert.Equal(0, status.GetProperty("pendingApprovals").GetArrayLength());
    }

    [Fact]
    public async Task Pause_for_a_duration_resumes_by_itself()
    {
        var (app, client) = await StartAsync(h => h.AddCondition("feed-down", c => c.ForSource("feed")));
        await using var _ = app;
        using var __ = client;

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/homeostat/pause?for=soon", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/homeostat/pause?for=00:30:00", null)).StatusCode);

        var status = await GetStatusAsync(client);
        Assert.True(status.GetProperty("paused").GetBoolean());
        Assert.StartsWith("paused until", status.GetProperty("pauseReason").GetString());
    }

    [Fact]
    public async Task Status_survives_an_unavailable_store()
    {
        var (app, client) = await StartAsync(h => h
            .UseStore(_ => new BrokenStore())
            .AddCondition("feed-down", c => c.ForSource("feed")));
        await using var _ = app;
        using var __ = client;

        var status = await GetStatusAsync(client);
        Assert.Equal("store down", status.GetProperty("storeError").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("budget").ValueKind);
        Assert.Single(status.GetProperty("conditions").EnumerateArray().ToList());
    }

    private sealed class BrokenStore : Homeostat.Persistence.IHomeostatStore
    {
        private static InvalidOperationException Down => new("store down");

        public ValueTask<Homeostat.Persistence.PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => throw Down;

        public ValueTask<Homeostat.Persistence.PlanState> UpdatePlanStateAsync(string key, Homeostat.Persistence.PlanStateUpdate update, CancellationToken cancellationToken = default) => throw Down;

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => throw Down;

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => throw Down;

        public ValueTask AppendRunAsync(Homeostat.Persistence.HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => throw Down;

        public ValueTask<IReadOnlyList<Homeostat.Persistence.HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) => throw Down;
    }

    [Fact]
    public async Task Endpoints_require_the_given_policy()
    {
        var (app, client) = await StartAsync(h => h.AddCondition("feed-down", c => c.ForSource("feed")), anonymousControl: false);
        await using var _ = app;
        using var __ = client;

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var pause = endpoints.Single(e => e.RoutePattern.RawText == "/homeostat/pause");
        var status = endpoints.Single(e => e.RoutePattern.RawText == "/homeostat/status");

        Assert.Equal("operations", pause.Metadata.GetMetadata<IAuthorizeData>()?.Policy);
        Assert.Equal("operations", status.Metadata.GetMetadata<IAuthorizeData>()?.Policy);
    }

    [Fact]
    public void Endpoints_refuse_to_map_without_a_policy_or_an_explicit_opt_out()
    {
        var app = WebApplication.CreateSlimBuilder().Build();

        Assert.Throws<ArgumentException>(() => app.MapHomeostatControl());
        Assert.Throws<ArgumentException>(() => app.MapHomeostatStatus());
    }

    [Fact]
    public async Task Homeostat_health_check_reports_attention()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat(h => h.AddCondition("feed-down", c => c.ForSource("feed")));
        builder.Services.AddHealthChecks().AddHomeostatCheck();
        await using var app = builder.Build();
        await app.StartAsync();

        var health = app.Services.GetRequiredService<HealthCheckService>();
        var control = app.Services.GetRequiredService<IHomeostatControl>();

        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync()).Status);

        control.Pause();
        var report = await health.CheckHealthAsync();
        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Contains("paused", report.Entries["homeostat"].Description);
        control.Resume();

        app.Services.GetRequiredService<ISignalPublisher>().TryPublish("feed", SignalSeverity.Unhealthy);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await health.CheckHealthAsync()).Status != HealthStatus.Degraded)
        {
            await Task.Delay(20, cts.Token);
        }

        var entry = (await health.CheckHealthAsync()).Entries["homeostat"];
        Assert.Contains("active: feed-down", entry.Description);
        Assert.Equal(new[] { "feed-down" }, (string[])entry.Data["active_conditions"]);
    }
}
