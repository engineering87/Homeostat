using System.Net;
using System.Text.Json;
using Homeostat.Cli;
using Homeostat.Conditions;
using Homeostat.Engine;
using Homeostat.Healing;
using Homeostat.Persistence;
using Homeostat.Recording;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Homeostat.Tests;

public class IntelligenceTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-01T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- predictive

    [Fact]
    public async Task Projected_threshold_triggers_before_the_value_crosses_it()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("disk-filling", c => c.ForSource("disk-used").WhenProjectedAbove(0.95, within: TimeSpan.FromHours(2), window: TimeSpan.FromHours(1)))
            .AddCondition("disk-steady", c => c.ForSource("disk-steady").WhenProjectedAbove(0.95, within: TimeSpan.FromHours(2), window: TimeSpan.FromHours(1))));

        // disk-used grows 1% every 5 minutes from 60%: 72% after an hour, 96% in two more hours. disk-steady stays at 90%.
        for (var minute = 0; minute <= 60; minute += 5)
        {
            var at = host.Time.GetUtcNow();
            host.Signals.TryPublish(new Signal("disk-used", SignalSeverity.Healthy, at) { Value = 0.60 + (minute / 5 * 0.01) });
            host.Signals.TryPublish(new Signal("disk-steady", SignalSeverity.Healthy, at) { Value = 0.90 });
            host.Time.Advance(TimeSpan.FromMinutes(5));
        }

        await host.WaitForConditionAsync("disk-filling", ConditionStatus.Active);
        Assert.Equal(ConditionStatus.Inactive, host.Control.GetConditions().Single(c => c.Name == "disk-steady").Status);
    }

    [Fact]
    public async Task Projected_rules_come_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Conditions:workers-draining:Source"] = "workers",
            ["Conditions:workers-draining:ProjectedBelow:Threshold"] = "2",
            ["Conditions:workers-draining:ProjectedBelow:Within"] = "30m",
            ["Conditions:workers-draining:ProjectedBelow:Window"] = "30m",
        }).Build();
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddConditionsFromConfiguration(config.GetSection("Conditions")));

        for (var minute = 0; minute <= 20; minute += 2)
        {
            host.Signals.TryPublish(new Signal("workers", SignalSeverity.Healthy, host.Time.GetUtcNow()) { Value = 20 - (minute * 0.5) });
            host.Time.Advance(TimeSpan.FromMinutes(2));
        }

        await host.WaitForConditionAsync("workers-draining", ConditionStatus.Active);
    }

    private static async Task<(HomeostatTestHost Host, SignalChannel Channel)> ProjectedHostAsync()
    {
        var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("disk-filling", c => c.ForSource("disk-used").WhenProjectedAbove(0.95, within: TimeSpan.FromHours(2), window: TimeSpan.FromHours(1))));
        var channel = host.Services.GetRequiredService<SignalChannel>();
        for (var minute = 0; minute <= 60; minute += 5)
        {
            host.Signals.TryPublish(new Signal("disk-used", SignalSeverity.Healthy, host.Time.GetUtcNow()) { Value = 0.60 + (minute / 5 * 0.01) });
            host.Time.Advance(TimeSpan.FromMinutes(5));
        }

        await host.WaitForConditionAsync("disk-filling", ConditionStatus.Active);
        return (host, channel);
    }

    [Fact]
    public async Task A_signal_without_a_value_does_not_clear_a_projection()
    {
        var (host, channel) = await ProjectedHostAsync();
        await using var _ = host;

        var processed = channel.Processed;
        host.Publish("disk-used", SignalSeverity.Unhealthy, "probe failed");     // no value: cannot be evaluated
        await PollyControlTests.WaitUntilAsync(() => channel.Processed > processed);

        Assert.Equal(ConditionStatus.Active, host.Control.GetConditions().Single().Status);
    }

    [Fact]
    public async Task A_projection_clears_soon_after_the_values_stop_rising()
    {
        var (host, channel) = await ProjectedHostAsync();
        await using var _ = host;

        // Fixed: the usage drops to 50% and stays there. The old ramp is still in the one-hour window.
        for (var i = 0; i < 5; i++)
        {
            host.Signals.TryPublish(new Signal("disk-used", SignalSeverity.Healthy, host.Time.GetUtcNow()) { Value = 0.50 });
            host.Time.Advance(TimeSpan.FromMinutes(5));
        }

        await host.WaitForConditionAsync("disk-filling", ConditionStatus.Inactive);
        Assert.True(channel.Processed <= 13 + 5);
    }

    [Fact]
    public void Recent_slope_ignores_the_older_values()
    {
        var series = new SignalSeries(TimeSpan.FromHours(1));
        for (var minute = 0; minute <= 60; minute += 5)
        {
            series.Add(new Signal("x", SignalSeverity.Healthy, Start.AddMinutes(minute)) { Value = minute < 40 ? minute : 40 - ((minute - 40) * 0.1) });
        }

        Assert.True(series.SlopePerMinute(5) > 0);           // the whole window still rises
        Assert.True(series.RecentSlopePerMinute(5) < 0);     // the latest values fall
        Assert.Null(new SignalSeries(TimeSpan.FromHours(1)).RecentSlopePerMinute(5));
    }

    // ---------------------------------------------------------------- adaptive

    private static HealingStep Step(string name, bool approval = false)
    {
        var plan = new HealingPlanBuilder(new ServiceCollection()).With(name, _ => { });
        if (approval)
        {
            plan.RequireApproval();
        }

        return plan.Build("x").Steps.Single();
    }

    private static HealingRunRecord Run(string condition, params (string Step, bool Healed)[] steps) => new()
    {
        ConditionName = condition,
        Outcome = HealingRunOutcome.Healed,
        StartedAt = Start,
        CompletedAt = Start,
        Steps = [.. steps.Select(s => new HealingStepRecord { Name = s.Step, Succeeded = true, Verification = s.Healed ? VerificationOutcome.Healed : VerificationOutcome.NotHealed })],
    };

    [Fact]
    public void Adaptive_order_puts_the_step_that_heals_first_but_never_ahead_of_an_approval()
    {
        HealingStep[] steps = [Step("renew"), Step("flush-dns"), Step("reset-pool"), Step("failover", approval: true), Step("restart")];
        var history = Enumerable.Range(0, 6).Select(_ => Run("feed-down", ("renew", false), ("flush-dns", false), ("reset-pool", true)))
            .Append(Run("other", ("restart", true)))
            .ToList();

        var ordered = AdaptiveOrder.Order(steps, history, "feed-down", minimumAttempts: 5).Select(s => s.Name).ToArray();

        Assert.Equal(["reset-pool", "renew", "flush-dns", "failover", "restart"], ordered);
        Assert.Equal(steps.Select(s => s.Name), AdaptiveOrder.Order(steps, history.Take(1), "feed-down", minimumAttempts: 5).Select(s => s.Name));   // too little evidence
    }

    [Fact]
    public void Adaptive_order_keeps_steps_without_enough_attempts_in_place()
    {
        // "probe-dns" was just added to the plan: never tried. It must not be pushed behind the others, nor ahead.
        HealingStep[] steps = [Step("renew"), Step("probe-dns"), Step("reset-pool")];
        var history = Enumerable.Range(0, 6).Select(_ => Run("feed-down", ("renew", false), ("reset-pool", true))).ToList();

        var ordered = AdaptiveOrder.Order(steps, history, "feed-down", minimumAttempts: 5).Select(s => s.Name).ToArray();

        Assert.Equal(["reset-pool", "probe-dns", "renew"], ordered);
    }

    [Fact]
    public async Task Adaptive_comes_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Conditions:feed-down:Source"] = "feed",
            ["Conditions:feed-down:Plan:Steps:0:Action"] = "switch:degraded",
            ["Conditions:feed-down:Plan:Adaptive:MinimumAttempts"] = "8",
            ["Conditions:disk-full:Source"] = "disk",
            ["Conditions:disk-full:Plan:Steps:0:Action"] = "switch:read-only",
            ["Conditions:disk-full:Plan:Adaptive"] = "true",
        }).Build();
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddConditionsFromConfiguration(config.GetSection("Conditions")));

        var plans = host.Services.GetRequiredService<ConditionSet>().Trackers.ToDictionary(t => t.Definition.Name, t => t.Definition.Plan!);
        Assert.Equal(8, plans["feed-down"].AdaptiveMinimumAttempts);
        Assert.Equal(5, plans["disk-full"].AdaptiveMinimumAttempts);
    }

    [Fact]
    public async Task Adaptive_plan_runs_the_learned_first_step()
    {
        var store = new InMemoryHomeostatStore();
        for (var i = 0; i < 5; i++)
        {
            await store.AppendRunAsync(Run("feed-down", ("renew", false), ("reset-pool", true)), 100);
        }

        await using var host = await HomeostatTestHost.StartAsync(h => h
            .UseStore(_ => store)
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                .With("renew", _ => { })
                .Then("reset-pool", _ => { })
                .Adaptive()
                .VerifyWith<BuiltInActionTests.AlwaysHealed>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal("reset-pool", run.Steps[0].Name);
    }

    // ---------------------------------------------------------------- recording and replay

    [Fact]
    public async Task Signals_are_recorded_by_day_and_old_files_are_deleted()
    {
        var directory = Directory.CreateTempSubdirectory("homeostat-rec-").FullName;
        try
        {
            var old = Path.Combine(directory, "signals-2026-01-01.jsonl");
            var notOurs = Path.Combine(directory, "signals-2026-01-01.backup.jsonl");     // same prefix and suffix
            await File.WriteAllTextAsync(old, "{}\n");
            await File.WriteAllTextAsync(notOurs, "{}\n");
            await using (var host = await HomeostatTestHost.StartAsync(
                h => h
                    .RecordSignals(o =>
                    {
                        o.Path = Path.Combine(directory, "signals-{date}.jsonl");
                        o.Sources.Add("feed");
                        o.RetentionDays = 7;
                        o.IncludeDetails = true;
                    })
                    .AddCondition("feed-down", c => c.ForSource("feed"))
                    .AddCondition("both", c => c.WhenAllActive("feed-down")),
                options: new HomeostatTestHostOptions { StartTime = Start }))
            {
                host.Signals.TryPublish(new Signal("feed", SignalSeverity.Unhealthy, Start) { Value = 12.5, Message = "timeout", Tags = new Dictionary<string, string> { ["region"] = "west" } });
                host.Signals.TryPublish(new Signal("cache", SignalSeverity.Healthy, Start));
                host.Signals.TryPublish(new Signal("feed", SignalSeverity.Healthy, Start.AddDays(1)));
                var channel = host.Services.GetRequiredService<SignalChannel>();
                await PollyControlTests.WaitUntilAsync(() => channel.Processed >= 3);
            }

            var day1 = RecordedSignal.Read(Path.Combine(directory, "signals-2026-11-01.jsonl")).ToList();
            var day2 = RecordedSignal.Read(Path.Combine(directory, "signals-2026-11-02.jsonl")).ToList();
            var first = Assert.Single(day1);
            Assert.Equal(("feed", SignalSeverity.Unhealthy, 12.5, "timeout"), (first.Source, first.Severity, first.Value!.Value, first.Message));
            Assert.Equal("west", first.Tags!["region"]);
            Assert.Single(day2);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(notOurs));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, "signals-2026-11-01.jsonl")));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Recordings_are_per_instance_and_leave_out_details_by_default()
    {
        var directory = Directory.CreateTempSubdirectory("homeostat-rec-").FullName;
        try
        {
            await using (var host = await HomeostatTestHost.StartAsync(
                h => h
                    .Configure(o => o.InstanceId = "web/1")
                    .RecordSignals(o => o.Path = Path.Combine(directory, "signals-{instance}-{date}.jsonl"))
                    .AddCondition("feed-down", c => c.ForSource("feed")),
                options: new HomeostatTestHostOptions { StartTime = Start }))
            {
                host.Signals.TryPublish(new Signal("feed", SignalSeverity.Unhealthy, Start) { Message = "timeout at 10.0.0.7", Tags = new Dictionary<string, string> { ["host"] = "db-3" } });
                var channel = host.Services.GetRequiredService<SignalChannel>();
                await PollyControlTests.WaitUntilAsync(() => channel.Processed >= 1);
            }

            var recorded = Assert.Single(RecordedSignal.Read(Path.Combine(directory, "signals-web_1-2026-11-01.jsonl")));
            Assert.Equal(("feed", "web/1"), (recorded.Source, recorded.Instance));
            Assert.Null(recorded.Message);
            Assert.Null(recorded.Tags);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Replay_stays_in_dry_run_even_if_the_configuration_post_configures_it_off()
    {
        var acted = false;
        var signals = Enumerable.Range(0, 5).Select(i => new RecordedSignal(Start.AddSeconds(i * 10), "feed", SignalSeverity.Unhealthy));

        var report = await HomeostatReplay.RunAsync(signals, h =>
        {
            h.Services.PostConfigure<HomeostatOptions>(o => o.DryRun = false);
            h.AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p.With("renew", _ => acted = true)));
        });

        Assert.False(acted);
        Assert.Equal(HealingRunOutcome.DryRun, Assert.Single(report.Runs).Outcome);
    }

    [Fact]
    public async Task Replay_shows_what_the_plans_would_have_done_without_acting()
    {
        var directory = Directory.CreateTempSubdirectory("homeostat-replay-").FullName;
        try
        {
            // Yesterday: the feed failed for two minutes, then recovered; a blip at noon lasted ten seconds.
            var signals = new List<RecordedSignal>();
            for (var s = 0; s <= 120; s += 10)
            {
                signals.Add(new(Start.AddSeconds(s), "weather-feed", SignalSeverity.Unhealthy, Message: "timeout"));
            }

            signals.Add(new(Start.AddSeconds(130), "weather-feed", SignalSeverity.Healthy));
            signals.Add(new(Start.AddHours(4), "weather-feed", SignalSeverity.Unhealthy));
            signals.Add(new(Start.AddHours(4).AddSeconds(10), "weather-feed", SignalSeverity.Healthy));
            var file = Path.Combine(directory, "signals.jsonl");
            await File.WriteAllLinesAsync(file, signals.Select(s => JsonSerializer.Serialize(s, RecordedSignal.Json)));
            var acted = false;
            var audit = Path.Combine(directory, "audit.jsonl");

            var report = await HomeostatReplay.RunFilesAsync([file], h => h
                .Configure(o => o.DryRun = false)                        // ignored: a replay never acts
                .AddThreadPoolStarvationDetector()                       // removed: only recorded signals
                .AddAuditLog(audit)                                      // removed: no side effects
                .AddCondition("feed-down", c => c
                    .ForSource("weather-feed")
                    .SustainedFor(TimeSpan.FromSeconds(30))
                    .Heal(p => p.With("renew-client", _ => acted = true).Then("failover", _ => acted = true))));

            Assert.False(acted);
            Assert.False(File.Exists(audit));
            Assert.Equal(16, report.Signals);
            var summary = Assert.Single(report.Conditions);
            Assert.Equal(1, summary.Activations);                       // the blip was too short
            Assert.Equal(1, summary.WouldHeal);
            Assert.Equal(["renew-client", "failover"], summary.PlanSteps);
            Assert.Equal(
                [ReplayEventKind.Activated, ReplayEventKind.WouldHeal, ReplayEventKind.Cleared],
                report.Events.Select(e => e.Kind));
            Assert.Equal(Start.AddSeconds(30), report.Events[0].At);
            Assert.Contains("would heal 1 times", report.ToText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------------------------------------------------------------- coverage

    [Fact]
    public async Task Coverage_report_shows_what_was_exercised()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                .With("renew", _ => { })
                .Then("failover", _ => { })
                .VerifyWith<BuiltInActionTests.AlwaysHealed>()))
            .AddCondition("disk-full", c => c.ForSource("disk").Heal(p => p.With("prune", _ => { })))
            .AddCondition("observed", c => c.ForSource("threadpool")));
        var since = host.Time.GetUtcNow();

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("feed-down");

        var report = await host.Control.GetCoverageAsync(since);

        var feed = report.Conditions.Single(c => c.Name == "feed-down");
        Assert.Equal((1, 1), (feed.Activations, feed.Runs));
        Assert.Equal(1, feed.Outcomes["Healed"]);
        Assert.Equal(["renew"], feed.StepsRun);
        Assert.Equal(["failover"], feed.StepsNeverRun);
        Assert.Equal(["disk-full", "observed"], report.NeverActivated.Order());
        Assert.Equal(["disk-full"], report.PlansNeverRun);
        Assert.Equal(1.0 / 3, report.StepCoverage, precision: 6);
        Assert.False(report.Truncated);
    }

    [Fact]
    public async Task Coverage_does_not_count_failed_steps_and_flags_a_truncated_history()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.RunHistorySize = 1)
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                .With("renew", _ => throw new InvalidOperationException("no"))
                .Then("failover", _ => { })
                .Cooldown(TimeSpan.Zero)
                .VerifyWith<BuiltInActionTests.AlwaysHealed>())));
        var since = host.Time.GetUtcNow();

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("feed-down");
        host.Time.Advance(TimeSpan.FromMinutes(1));
        host.Publish("feed", SignalSeverity.Unhealthy);
        await PollyControlTests.WaitUntilAsync(() => host.Control.GetRecentRunsAsync().AsTask().Result is [{ } latest] && latest.CompletedAt > since);

        var report = await host.Control.GetCoverageAsync(since.AddSeconds(-1));
        var feed = Assert.Single(report.Conditions);

        Assert.Equal(["failover"], feed.StepsRun);
        Assert.Equal(["renew"], feed.StepsFailed);
        Assert.Equal(0.5, report.StepCoverage);
        Assert.True(report.Truncated);
    }

    [Fact]
    public async Task Coverage_is_served_and_the_cli_can_fail_a_game_day_below_a_threshold()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat(h => h
            .Configure(o => o.DryRun = false)
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                .With("renew", _ => { }).Then("failover", _ => { }).VerifyWith<BuiltInActionTests.AlwaysHealed>())));
        var app = builder.Build();
        app.MapHomeostatStatus(allowAnonymous: true);
        await app.StartAsync();
        await using var _ = app;

        app.Services.GetRequiredService<ISignalPublisher>().TryPublish("feed", SignalSeverity.Unhealthy);
        await PollyControlTests.WaitUntilAsync(() => app.Services.GetRequiredService<IHomeostatControl>().GetRecentRunsAsync().AsTask().Result.Count == 1);

        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.First()) };
        using var response = await client.GetAsync("/homeostat/coverage?last=01:00:00");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0.5, json.GetProperty("stepCoverage").GetDouble());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/homeostat/coverage?since=yesterday")).StatusCode);

        var output = new StringWriter();
        var code = await HomeostatCli.InvokeAsync(["coverage", "--last", "1h", "--fail-below", "0.8", "--url", app.Urls.First()],
            output, new StringWriter(), new HttpClientHandler { UseProxy = false });
        Assert.Equal(HomeostatCli.LowCoverage, code);
        Assert.Contains("never ran: failover", output.ToString());

        var invalid = await HomeostatCli.InvokeAsync(["coverage", "--fail-below", "80", "--url", app.Urls.First()],
            new StringWriter(), new StringWriter(), new HttpClientHandler { UseProxy = false });
        Assert.NotEqual(HomeostatCli.LowCoverage, invalid);
        Assert.NotEqual(0, invalid);
    }
}
