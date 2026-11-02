using Homeostat.Conditions;
using Homeostat.Healing;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Tests;

public class ConfigurationTests
{
    private static IConfigurationRoot Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public async Task Conditions_and_plans_come_from_configuration()
    {
        var config = Config(new()
        {
            ["Conditions:feed-down:Source"] = "feed",
            ["Conditions:feed-down:Severity"] = "Degraded",
            ["Conditions:feed-down:SustainedFor"] = "0s",
            ["Conditions:feed-down:Plan:Steps:0:Action"] = "record",
            ["Conditions:feed-down:Plan:Steps:1:Action"] = "switch:serve-cached",
            ["Conditions:feed-down:Plan:Steps:1:TurnOffWhenCleared"] = "true",
            ["Conditions:feed-down:Plan:Cooldown"] = "5m",
        });

        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .MapAction<EngineTests.RecordingAction>("record")
                .AddConditionsFromConfiguration(config.GetSection("Conditions")),
            services => services.AddSingleton<EngineTests.Recorder>());

        // The problem persists, so the default verifier says NotHealed and the plan escalates to the switch.
        using var timer = host.Time.CreateTimer(_ => host.Publish("feed", SignalSeverity.Degraded), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal(new[] { nameof(EngineTests.RecordingAction), "Turn on switch 'serve-cached'" }, run.Steps.Select(s => s.Name).ToArray());
        Assert.Equal(1, host.Services.GetRequiredService<EngineTests.Recorder>().Executions);
        Assert.True(host.Switches.IsOn("serve-cached"));
    }

    [Fact]
    public async Task Approval_undo_scope_backoff_and_composites_are_configurable()
    {
        var config = Config(new()
        {
            ["Conditions:db:Source"] = "db",
            ["Conditions:cache:Source"] = "cache",
            ["Conditions:both:AllActive:0"] = "db",
            ["Conditions:both:AllActive:1"] = "cache",
            ["Conditions:both:ClearAfter"] = "1m",
            ["Conditions:both:SuppressedBy"] = "network",
            ["Conditions:network:Source"] = "network",
            ["Conditions:network:Occurrences:Count"] = "3",
            ["Conditions:network:Occurrences:Within"] = "00:05:00",
            ["Conditions:both:Plan:Steps:0:Action"] = "record",
            ["Conditions:both:Plan:Steps:0:RequireApproval"] = "true",
            ["Conditions:both:Plan:Steps:0:ApprovalTimeout"] = "10m",
            ["Conditions:both:Plan:Steps:0:UndoAction"] = "record",
            ["Conditions:both:Plan:Scope"] = "Cluster",
            ["Conditions:both:Plan:CooldownBackoff:Multiplier"] = "2",
            ["Conditions:both:Plan:CooldownBackoff:Max"] = "1h",
        });

        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .MapAction<EngineTests.RecordingAction>("record")
                .AddConditionsFromConfiguration(config.GetSection("Conditions")),
            services => services.AddSingleton<EngineTests.Recorder>());

        var definition = host.Services.GetRequiredService<ConditionSet>().Get("both")!.Definition;
        Assert.Equal(CompositeMode.All, definition.Composite);
        Assert.Equal(new[] { "db", "cache" }, definition.DependsOn.ToArray());
        Assert.Equal(TimeSpan.FromMinutes(1), definition.ClearAfter);
        Assert.Equal("network", Assert.Single(definition.SuppressedBy));
        var plan = definition.Plan!;
        Assert.Equal(PlanScope.Cluster, plan.Scope);
        Assert.Equal(2, plan.CooldownBackoff);
        Assert.Equal(TimeSpan.FromHours(1), plan.MaxCooldown);
        var step = Assert.Single(plan.Steps);
        Assert.True(step.RequiresApproval);
        Assert.Equal(TimeSpan.FromMinutes(10), step.ApprovalTimeout);
        Assert.Equal(nameof(EngineTests.RecordingAction), step.UndoName);
    }

    [Theory]
    [InlineData("Plan:Steps:0:Action", "missing-action", "unknown action 'missing-action'")]
    [InlineData("SustainedFor", "soon", "feed-down")]
    [InlineData("Severity", "Terrible", "feed-down")]
    [InlineData("SustainFor", "30s", "is not a known setting")]
    [InlineData("Plan:Steps:0:RequireAproval", "true", "is not a known setting")]
    [InlineData("ClearAfter", "30", "needs a unit")]
    [InlineData("ValueAbove", "0.5", "only one of Severity, ValueAbove, ValueBelow")]
    [InlineData("RisingFasterThan:PerMinute", "1", "only one of")]
    [InlineData("AnomalouslyHigh:Window", "1h", "only one of")]
    public async Task Invalid_configuration_fails_at_startup_with_the_path(string key, string value, string expected)
    {
        var config = Config(new()
        {
            ["Conditions:feed-down:Source"] = "feed",
            ["Conditions:feed-down:Plan:Steps:0:Action"] = "switch:x",
            ["Conditions:feed-down:Severity"] = "Unhealthy",
            [$"Conditions:feed-down:{key}"] = value,
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HomeostatTestHost.StartAsync(h => h
            .AddConditionsFromConfiguration(config.GetSection("Conditions"))));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Trend_and_anomaly_rules_come_from_configuration()
    {
        var config = Config(new()
        {
            ["Conditions:leak:Source"] = "memory",
            ["Conditions:leak:RisingFasterThan:PerMinute"] = "1",
            ["Conditions:leak:RisingFasterThan:Window"] = "10m",
            ["Conditions:leak:RisingFasterThan:MinimumSamples"] = "3",
            ["Conditions:spike:Source"] = "latency",
            ["Conditions:spike:AnomalouslyHigh:ZScore"] = "3",
            ["Conditions:spike:AnomalouslyHigh:Window"] = "1h",
            ["Conditions:spike:AnomalouslyHigh:MinimumHistory"] = "10",
            ["Conditions:drain:Source"] = "free-disk",
            ["Conditions:drain:FallingFasterThan:PerMinute"] = "1",
            ["Conditions:drain:FallingFasterThan:Window"] = "10m",
        });

        await using var host = await HomeostatTestHost.StartAsync(h => h.AddConditionsFromConfiguration(config.GetSection("Conditions")));

        // Memory rising by 2 per minute.
        for (var minute = 0; minute <= 6; minute++) // spans more than half the window
        {
            host.Signals.TryPublish(new Signal("memory", SignalSeverity.Healthy, host.Time.GetUtcNow()) { Value = 10 + (2 * minute) });
            host.Signals.TryPublish(new Signal("free-disk", SignalSeverity.Healthy, host.Time.GetUtcNow()) { Value = 100 - (2 * minute) });
            host.Time.Advance(TimeSpan.FromMinutes(1));
        }

        // Steady latency, then a spike.
        for (var i = 0; i < 12; i++)
        {
            host.Signals.TryPublish(new Signal("latency", SignalSeverity.Healthy, host.Time.GetUtcNow()) { Value = 100 + (i % 3) });
            host.Time.Advance(TimeSpan.FromSeconds(10));
        }

        host.Signals.TryPublish(new Signal("latency", SignalSeverity.Healthy, host.Time.GetUtcNow()) { Value = 900 });

        await PollyControlTests.WaitUntilAsync(() =>
            host.Control.GetConditions().Count(c => c.Status == Conditions.ConditionStatus.Active) == 3);
    }

    [Theory]
    [InlineData("RisingFasterThan:Window", "10m", "RisingFasterThan:PerMinute' is required")]
    [InlineData("AnomalouslyLow:Score", "3", "is not a known setting")]
    public async Task Incomplete_trend_and_anomaly_rules_are_rejected(string key, string value, string expected)
    {
        var config = Config(new()
        {
            ["Conditions:x:Source"] = "x",
            [$"Conditions:x:{key}"] = value,
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HomeostatTestHost.StartAsync(h => h
            .AddConditionsFromConfiguration(config.GetSection("Conditions"))));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Configuration_changes_are_applied_without_losing_state()
    {
        var config = Config(new()
        {
            ["Conditions:feed-down:Source"] = "feed",
        });

        await using var host = await HomeostatTestHost.StartAsync(h => h.AddConditionsFromConfiguration(config.GetSection("Conditions")));
        var reloader = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Homeostat.Configuration.ConditionReloader>().Single();

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForConditionAsync("feed-down", ConditionStatus.Active);

        // Add a condition and change the existing one.
        config["Conditions:feed-down:ClearAfter"] = "1h";
        config["Conditions:queue-growing:Source"] = "queue";
        config.Reload();

        await WaitUntilAsync(() => host.Control.GetConditions().Any(c => c.Name == "queue-growing"));
        Assert.Equal(ConditionStatus.Active, host.Control.GetConditions().Single(c => c.Name == "feed-down").Status);

        // ClearAfter now applies: a healthy signal does not clear at once.
        host.Publish("feed", SignalSeverity.Healthy);
        await Task.Delay(100);
        Assert.Equal(ConditionStatus.Active, host.Control.GetConditions().Single(c => c.Name == "feed-down").Status);

        // A broken change is ignored.
        config["Conditions:queue-growing:Plan:Steps:0:Action"] = "nope";
        reloader.Reload();
        Assert.Contains(host.Control.GetConditions(), c => c.Name == "queue-growing");
    }

    [Fact]
    public async Task Unchanged_conditions_keep_their_definition_and_rule_history_across_reloads()
    {
        var config = Config(new()
        {
            ["Conditions:flaky:Source"] = "feed",
            ["Conditions:flaky:Occurrences:Count"] = "3",
            ["Conditions:flaky:Occurrences:Within"] = "10m",
            ["Conditions:other:Source"] = "other",
        });

        await using var host = await HomeostatTestHost.StartAsync(h => h.AddConditionsFromConfiguration(config.GetSection("Conditions")));
        var set = host.Services.GetRequiredService<ConditionSet>();
        var before = set.Get("flaky")!.Definition;

        host.Publish("feed", SignalSeverity.Unhealthy);
        host.Publish("feed", SignalSeverity.Unhealthy);

        config["Conditions:other:SustainedFor"] = "1m";   // an unrelated change
        config.Reload();
        await WaitUntilAsync(() => set.Get("other")!.Definition.SustainFor == TimeSpan.FromMinutes(1));

        Assert.Same(before, set.Get("flaky")!.Definition);
        host.Publish("feed", SignalSeverity.Unhealthy);   // third match: the history survived the reload
        await host.WaitForConditionAsync("flaky", ConditionStatus.Active);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }
}
