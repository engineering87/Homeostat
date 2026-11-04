using System.Net;
using System.Text;
using Homeostat.Conditions;
using Homeostat.Healing;
using Homeostat.Metrics;
using Homeostat.Persistence;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Homeostat.Tests;

/// <summary>Defects found in the review before 1.0, one test each.</summary>
public class ConsolidationTests
{
    private static string TempFile() => Path.Combine(Directory.CreateTempSubdirectory("homeostat-").FullName, "state.json");

    [Fact]
    public async Task A_file_store_change_that_cannot_be_written_is_undone()
    {
        var path = TempFile();
        using var store = new FileHomeostatStore(path);
        var now = DateTimeOffset.UtcNow;
        Assert.True(await store.TryConsumeBudgetAsync(now, 10, TimeSpan.FromHours(1)));

        // The temporary file cannot be created: the write fails.
        Directory.CreateDirectory(path + ".tmp");
        await Assert.ThrowsAnyAsync<Exception>(() => store.TryConsumeBudgetAsync(now, 10, TimeSpan.FromHours(1)).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => store.AppendConditionEventAsync(
            new ConditionEventRecord { ConditionName = "feed-down", Kind = ConditionEventKind.Activated, At = now, Instance = "a" }, 10).AsTask());

        Directory.Delete(path + ".tmp");
        Assert.Equal(1, await store.GetBudgetUsedAsync(now, TimeSpan.FromHours(1)));
        Assert.Empty(await store.GetConditionEventsAsync(10));
    }

    [Fact]
    public void A_second_file_store_on_the_same_file_fails_at_once()
    {
        var path = TempFile();
        using var first = new FileHomeostatStore(path);

        var error = Assert.Throws<IOException>(() => new FileHomeostatStore(path));
        Assert.Contains("in use by another process", error.Message);

        first.Dispose();
        using var second = new FileHomeostatStore(path);
    }

    [Fact]
    public async Task A_file_with_null_records_does_not_prevent_startup()
    {
        var path = TempFile();
        await File.WriteAllTextAsync(path, """{ "plans": { "a": null }, "switches": [null], "runs": [null], "conditions": [null], "budget": [] }""");

        using var store = new FileHomeostatStore(path);

        Assert.Empty(await store.GetRecentRunsAsync(10));
        Assert.Empty(await store.GetConditionEventsAsync(10));
        Assert.Empty((await store.GetControlStateAsync()).Switches);
        Assert.Equal(PlanState.Empty, await store.GetPlanStateAsync("a"));
    }

    [Fact]
    public void Two_triggering_signals_far_apart_do_not_sustain_a_condition()
    {
        var tracker = new ConditionTracker(new ConditionBuilder("feed-down", new ServiceCollection())
            .ForSource("feed").SustainedFor(TimeSpan.FromMinutes(5)).Build());
        var start = DateTimeOffset.UtcNow;

        Assert.Equal(ConditionTransition.None, tracker.Observe(new Signal("feed", SignalSeverity.Unhealthy, start)));
        Assert.Equal(ConditionTransition.None, tracker.Observe(new Signal("feed", SignalSeverity.Unhealthy, start.AddHours(1))));
        Assert.Equal(ConditionStatus.Pending, tracker.Status);

        // Signals that keep coming sustain it from the restart.
        Assert.Equal(ConditionTransition.None, tracker.Observe(new Signal("feed", SignalSeverity.Unhealthy, start.AddHours(1).AddMinutes(3))));
        Assert.Equal(ConditionTransition.Activated, tracker.Observe(new Signal("feed", SignalSeverity.Unhealthy, start.AddHours(1).AddMinutes(5))));
    }

    [Fact]
    public void A_slow_source_still_sustains_a_condition()
    {
        var tracker = new ConditionTracker(new ConditionBuilder("feed-down", new ServiceCollection())
            .ForSource("feed").SustainedFor(TimeSpan.FromSeconds(30)).Build());
        var start = DateTimeOffset.UtcNow;

        tracker.Observe(new Signal("feed", SignalSeverity.Unhealthy, start));
        Assert.Equal(ConditionTransition.Activated, tracker.Observe(new Signal("feed", SignalSeverity.Unhealthy, start.AddMinutes(2))));
    }

    [Fact]
    public async Task The_cooldown_backoff_never_shortens_the_cooldown()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p.With("renew", _ => { }).Cooldown(TimeSpan.FromMinutes(10)).CooldownBackoff(2, TimeSpan.FromMinutes(1)))));
        var runner = host.Services.GetRequiredService<Engine.HealingRunner>();
        var plan = host.Services.GetRequiredService<ConditionSet>().Get("feed-down")!.Definition.Plan!;

        Assert.Equal(TimeSpan.FromMinutes(10), runner.EffectiveCooldown(plan, new PlanState { ConsecutiveFailures = 3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ConditionBuilder("x", new ServiceCollection()).Heal(p => p.Cooldown(TimeSpan.FromDays(60))));
    }

    [Theory]
    [InlineData("Severity", "7", "expected one of")]
    [InlineData("Severity", "Degraded,Unhealthy", "expected one of")]
    [InlineData("Plan:Scope", "5", "expected one of")]
    [InlineData("Plan:Steps:0:ApprovalTimeout", "5m", "needs \"RequireApproval\"")]
    [InlineData("Plan:Steps:0:TurnOffWhenCleared", "false", "applies only to a 'switch:<name>' action")]
    public async Task Configuration_refuses_values_that_would_be_ignored(string key, string value, string expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Conditions:feed-down:Source"] = "feed",
            ["Conditions:feed-down:Plan:Steps:0:Action"] = "reload-configuration",
            [$"Conditions:feed-down:{key}"] = value,
        }).Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HomeostatTestHost.StartAsync(h => h
            .AddConditionsFromConfiguration(config.GetSection("Conditions"))));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Configuration_refuses_both_composite_modes()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Conditions:a:Source"] = "a",
            ["Conditions:b:Source"] = "b",
            ["Conditions:both:AllActive:0"] = "a",
            ["Conditions:both:AnyActive:0"] = "b",
        }).Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HomeostatTestHost.StartAsync(h => h
            .AddConditionsFromConfiguration(config.GetSection("Conditions"))));
        Assert.Contains("only one of AllActive, AnyActive", error.Message);
    }

    [Fact]
    public void An_observable_series_that_stops_reporting_no_longer_counts()
    {
        var watch = new MetricWatch("db.client.connections.usage", null, "db:pool", MetricStatistic.Last, [], null, null, null, null, null, minimumCount: 1);
        var now = DateTimeOffset.UtcNow;

        watch.Record(10, null, InstrumentKind.ObservableUpDownCounter, "pool=a");
        watch.Record(10, null, InstrumentKind.ObservableUpDownCounter, "pool=b");
        Assert.Equal(20, watch.Collect(now, TimeSpan.FromSeconds(10))!.Value);

        watch.Record(10, null, InstrumentKind.ObservableUpDownCounter, "pool=a");
        Assert.Equal(10, watch.Collect(now, TimeSpan.FromSeconds(10))!.Value);
    }

    [Fact]
    public async Task Detector_options_are_validated_at_startup()
    {
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => HomeostatTestHost.StartAsync(h => h
            .AddThreadPoolStarvationDetector(o => o.UnhealthyLatency = TimeSpan.FromMilliseconds(-1))));
        Assert.Contains("ThreadPoolDetectorOptions", error.Message);
    }

    [Fact]
    public async Task A_skipped_compensating_action_is_reported_as_not_run()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.MaxActionsPerWindow = 1)
            .AddCondition("feed-down", c => c
                .ForSource("feed")
                .Heal(p => p.With("switch-provider", _ => { }).UndoWith("switch-back", (_, _) => Task.CompletedTask).VerifyWith<CachingTests.NeverHealed>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        // The step used the only unit of the budget: the undo is skipped, not failed.
        Assert.Null(Assert.Single(run.Steps).UndoSucceeded);
    }

    [Fact]
    public async Task Alerts_posted_by_a_browser_form_are_refused()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHomeostat();
        await using var app = builder.Build();
        app.MapHomeostatAlerts(allowAnonymous: true);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        const string Json = """{ "source": "db", "severity": "Unhealthy" }""";

        using var form = await client.PostAsync("/homeostat/alerts/signals", new StringContent(Json, Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, form.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/homeostat/alerts/signals")
        {
            Content = new StringContent(Json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSite = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
    }

    [Fact]
    public void Latency_thresholds_are_durations_over_the_millisecond_value()
    {
        var options = new Probes.LatencyProbeOptions { DegradedLatency = TimeSpan.FromMilliseconds(250), UnhealthyLatency = TimeSpan.FromSeconds(2) };

        Assert.Equal((250d, 2_000d), (options.DegradedAbove!.Value, options.UnhealthyAbove!.Value));
        options.DegradedAbove = 100;
        Assert.Equal(TimeSpan.FromMilliseconds(100), options.DegradedLatency);
    }

    [Fact]
    public void A_maintenance_window_cannot_change_once_registered()
    {
        Guardrails.MaintenanceWindow? window = null;
        new ServiceCollection().AddHomeostat(h => h.AddMaintenanceWindow("nightly", w => window = w.Daily(new TimeOnly(2, 0), TimeSpan.FromHours(1))));

        Assert.Throws<InvalidOperationException>(() => window!.On(DayOfWeek.Monday));
    }

    [Fact]
    public async Task Steps_and_run_share_the_run_id_and_the_run_names_its_instance()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.InstanceId = "web-7")
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p.With("renew", _ => { }).VerifyWith<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal("web-7", run.Instance);
        Assert.False(string.IsNullOrEmpty(run.RunId));
        Assert.All(run.Steps, s => Assert.Equal(run.RunId, s.RunId));
    }
}
