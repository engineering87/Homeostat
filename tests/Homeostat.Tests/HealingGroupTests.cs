using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using Homeostat.Conditions;
using Homeostat.Diagnostics;
using Homeostat.Guardrails;
using Homeostat.Healing;
using Homeostat.Persistence;
using Homeostat.Redis;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Homeostat.Tests;

/// <summary>
/// Healing groups: a named set of conditions (one module of a modular monolith) with its own label, concurrency limit,
/// action budget, pause and maintenance windows, on top of the global guardrails (decisions G1 to G5).
/// </summary>
public class HealingGroupTests
{
    private static readonly TimeSpan Forever = TimeSpan.FromDays(1);

    // ---------------------------------------------------------------- G1: the label

    [Fact]
    public async Task A_condition_in_a_group_labels_its_snapshot_approval_run_steps_events_and_stored_record()
    {
        var store = new InMemoryHomeostatStore();
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .UseStore(_ => store)
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")
                .Heal(p => p.With("recycle", _ => { }).RequireApproval().UseVerifier<EngineTests.AlwaysHealedVerifier>())));

        Assert.Equal("reports", host.Control.GetConditions().Single().Group);

        host.Publish("reports", SignalSeverity.Unhealthy);
        var request = await host.WaitForApprovalAsync("reports-slow");
        Assert.Equal("reports", request.Group);
        Assert.Equal("reports", Assert.Single(host.Approvals.GetPending()).Group);
        Assert.True(host.Approvals.Approve(request.Id, "on-call"));

        var run = await host.WaitForRunAsync("reports-slow");
        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Equal("reports", run.Group);
        Assert.Equal("reports", Assert.Single(run.Steps).Group);
        Assert.Equal("reports", (await host.Control.GetRecentRunsAsync()).Single().Group);

        var events = new Notifications.HomeostatEventFactory("app", TimeProvider.System);
        Assert.Equal("reports", events.Run(run).Group);
        Assert.Equal("reports", events.Step(run.Steps[0]).Group);
        Assert.Equal("reports", events.ApprovalRequested(request).Group);
        Assert.Equal("reports", events.Activated(host.Control.GetConditions().Single()).Group);

        var group = Assert.Single(host.Control.GetGroups());
        Assert.Equal("reports", group.Name);
        Assert.Equal(["reports-slow"], group.Conditions);
        Assert.False(group.IsPaused);
    }

    [Fact]
    public async Task Without_groups_nothing_changes()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p.With("renew", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Null(run.Group);
        Assert.Null(run.Steps[0].Group);
        Assert.Null(host.Control.GetConditions().Single().Group);
        Assert.Null((await host.Control.GetRecentRunsAsync()).Single().Group);
        Assert.Empty(host.Control.GetGroups());
        Assert.Empty(host.Services.GetRequiredService<Engine.SharedControl>().Current.GroupPauses);
        Assert.Null((await host.Control.GetBudgetAsync()).Group);
    }

    [Fact]
    public void Payloads_without_a_group_are_written_as_before()
    {
        var options = HomeostatJsonContext.CreateOptions();
        var at = DateTimeOffset.Parse("2026-11-04T10:00:00Z");
        var plain = new Notifications.HomeostatEvent { Kind = Notifications.HomeostatEventKind.RunCompleted, Timestamp = at, Application = "app", Condition = "feed-down", Summary = "s" };
        var record = new HealingRunRecord { ConditionName = "feed-down", Outcome = HealingRunOutcome.Healed, StartedAt = at, CompletedAt = at };

        Assert.False(System.Text.Json.JsonSerializer.Serialize(plain, options.TypeInfo<Notifications.HomeostatEvent>()).Contains("\"group\"", StringComparison.Ordinal));
        Assert.False(System.Text.Json.JsonSerializer.Serialize(record, options.TypeInfo<HealingRunRecord>()).Contains("\"group\"", StringComparison.Ordinal));
        Assert.Contains("\"group\":\"reports\"", System.Text.Json.JsonSerializer.Serialize(plain with { Group = "reports" }, options.TypeInfo<Notifications.HomeostatEvent>()));

        // Records stored before the group existed read as without a group.
        var old = System.Text.Json.JsonSerializer.Deserialize("""{"conditionName":"feed-down","outcome":"Healed","startedAt":"2026-11-04T10:00:00Z","completedAt":"2026-11-04T10:00:00Z"}""", options.TypeInfo<HealingRunRecord>());
        Assert.Null(old!.Group);
    }

    [Fact]
    public void Group_names_are_validated_and_a_second_InGroup_replaces_the_first()
    {
        var builder = new ConditionBuilder("reports-slow", new ServiceCollection()).ForSource("reports");
        Assert.Throws<ArgumentException>(() => builder.InGroup(""));
        Assert.Throws<ArgumentException>(() => builder.InGroup("  "));
        Assert.Throws<ArgumentException>(() => builder.InGroup("reports\nfake log line"));
        Assert.Throws<ArgumentException>(() => builder.InGroup(new string('r', 201)));
        Assert.Equal(new string('r', 200), new ConditionBuilder("x", new ServiceCollection()).ForSource("x").InGroup(new string('r', 200)).Build().Group);

        Assert.Equal("exports", builder.InGroup("reports").InGroup("exports").Build().Group);

        var services = new ServiceCollection();
        services.AddHomeostat(h =>
        {
            Assert.Throws<ArgumentException>(() => h.ConfigureGroup("bad\rname", _ => { }));
            Assert.Throws<ArgumentException>(() => h.AddMaintenanceWindow("w", w => w.Daily(new TimeOnly(1, 0), TimeSpan.FromHours(1)).ForGroups("ok", "")));
            Assert.Throws<ArgumentException>(() => h.AddMaintenanceWindow("w2", w => w.Daily(new TimeOnly(1, 0), TimeSpan.FromHours(1)).ForGroups()));
        });
    }

    [Fact]
    public async Task The_group_comes_from_configuration_and_a_reload_can_change_it()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Conditions:reports-slow:Source"] = "reports",
            ["Conditions:reports-slow:Group"] = "reports",
            ["Conditions:feed-down:Source"] = "feed",
        }).Build();

        await using var host = await HomeostatTestHost.StartAsync(h => h.AddConditionsFromConfiguration(config.GetSection("Conditions")));
        Assert.Equal("reports", host.Control.GetConditions().Single(c => c.Name == "reports-slow").Group);
        Assert.Null(host.Control.GetConditions().Single(c => c.Name == "feed-down").Group);

        config["Conditions:reports-slow:Group"] = "exports";
        config.Reload();
        await PollyControlTests.WaitUntilAsync(() => host.Control.GetConditions().Single(c => c.Name == "reports-slow").Group == "exports");

        // A group name that is not valid is refused like any other invalid setting.
        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Conditions:reports-slow:Source"] = "reports",
            ["Conditions:reports-slow:Group"] = new string('r', 201),
        }).Build();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => HomeostatTestHost.StartAsync(h => h
            .AddConditionsFromConfiguration(invalid.GetSection("Conditions"), failOnInvalid: true)));
        Assert.Contains("healing group", error.Message);
    }

    [Fact]
    public async Task Group_options_are_validated_at_startup_and_applied_in_order()
    {
        await Assert.ThrowsAnyAsync<OptionsValidationException>(() => HomeostatTestHost.StartAsync(h => h
            .ConfigureGroup("reports", g => g.MaxActionsPerWindow = 0)
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports"))));
        await Assert.ThrowsAnyAsync<OptionsValidationException>(() => HomeostatTestHost.StartAsync(h => h
            .ConfigureGroup("reports", g => g.ActionBudgetWindow = TimeSpan.FromDays(60))
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports"))));
        await Assert.ThrowsAnyAsync<OptionsValidationException>(() => HomeostatTestHost.StartAsync(h => h
            .ConfigureGroup("reports", g => g.MaxConcurrentHealings = -1)
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports"))));

        var logs = new LogCollector();
        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .Configure(o => o.ActionBudgetWindow = TimeSpan.FromMinutes(30))
                .ConfigureGroup("reports", g => { g.MaxActionsPerWindow = 3; g.MaxConcurrentHealings = 2; })
                .ConfigureGroup("reports", g => g.MaxConcurrentHealings = 1)
                .ConfigureGroup("unused", g => g.MaxActionsPerWindow = 1)
                .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")),
            services => services.AddSingleton<ILoggerProvider>(logs));

        var reports = host.Control.GetGroups().Single(g => g.Name == "reports");
        Assert.Equal(3, reports.MaxActionsPerWindow);
        Assert.Equal(1, reports.MaxConcurrentHealings);
        Assert.Equal(TimeSpan.FromMinutes(30), reports.ActionBudgetWindow);

        // A configured group without conditions is allowed, listed and said once at startup.
        Assert.Empty(host.Control.GetGroups().Single(g => g.Name == "unused").Conditions);
        await PollyControlTests.WaitUntilAsync(() => logs.Messages(LogLevel.Information).Any(m => m.Contains("unused")));
        Assert.Single(logs.Messages(LogLevel.Information), m => m.Contains("Healing group unused is configured, but no condition is in it yet."));
        Assert.DoesNotContain(logs.Messages(LogLevel.Information), m => m.Contains("Healing group reports is configured"));
    }

    [Fact]
    public async Task Metrics_carry_the_group_tag_only_for_a_condition_in_a_group_and_the_run_logs_carry_it_in_their_scope()
    {
        var measured = new ConcurrentQueue<(string Instrument, string? Condition, string? Group)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == HomeostatDiagnostics.Name)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        void Record(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? condition = null, group = null;
            foreach (var tag in tags)
            {
                condition = tag.Key == "condition" ? tag.Value as string : condition;
                group = tag.Key == "group" ? tag.Value as string : group;
            }

            measured.Enqueue((instrument.Name, condition, group));
        }

        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Record(instrument, tags));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Record(instrument, tags));
        listener.Start();

        var logs = new LogCollector();
        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .AddCondition("tagged-reports-slow", c => c.ForSource("reports").InGroup("reports")
                    .Heal(p => p.With("recycle", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>()))
                .AddCondition("tagged-feed-down", c => c.ForSource("feed")
                    .Heal(p => p.With("renew", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>())),
            services => services.AddSingleton<ILoggerProvider>(logs));

        host.Publish("reports", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("tagged-reports-slow");
        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("tagged-feed-down");

        string[] instruments = ["homeostat.healing.runs", "homeostat.healing.actions", "homeostat.healing.outcomes", "homeostat.healing.duration"];
        foreach (var instrument in instruments)
        {
            Assert.Contains(measured, m => m.Instrument == instrument && m.Condition == "tagged-reports-slow" && m.Group == "reports");
            Assert.Contains(measured, m => m.Instrument == instrument && m.Condition == "tagged-feed-down");
            Assert.DoesNotContain(measured, m => m.Instrument == instrument && m.Condition == "tagged-feed-down" && m.Group is not null);
        }

        // The message templates are unchanged; the group is in the scope of the run's entries.
        var executing = logs.Entries.Where(e => e.Message.StartsWith("Condition tagged-reports-slow: executing step", StringComparison.Ordinal)).ToArray();
        Assert.Contains(executing, e => e.Scope.Contains(new KeyValuePair<string, object?>("HealingGroup", "reports")));
        Assert.All(
            logs.Entries.Where(e => e.Message.StartsWith("Condition tagged-feed-down: executing step", StringComparison.Ordinal)),
            e => Assert.DoesNotContain(e.Scope, s => s.Key == "HealingGroup"));
    }

    // ---------------------------------------------------------------- G2: concurrency, group slot then global slot

    [Fact]
    public async Task A_busy_group_does_not_block_another_group()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.MaxConcurrentHealings = 2)
            .ConfigureGroup("reports", g => g.MaxConcurrentHealings = 1)
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")
                .Heal(p => p.With("recycle", async (_, _) => { started.TrySetResult(); await gate.Task; }).ActionTimeout(Forever)
                    .UseVerifier<EngineTests.AlwaysHealedVerifier>()))
            .AddCondition("reports-export-stuck", c => c.ForSource("exports").InGroup("reports")
                .Heal(p => p.With("restart-export", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>()))
            .AddCondition("ingest-lag", c => c.ForSource("ingest").InGroup("ingest")
                .Heal(p => p.With("scale-ingest", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("reports", SignalSeverity.Unhealthy);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The second run of the busy group waits for its group's slot without holding a global one...
        host.Publish("exports", SignalSeverity.Unhealthy);
        await Task.Delay(200);
        Assert.Equal(1, GlobalSlotsFree(host));

        // ...so the second global slot is free for another group.
        host.Publish("ingest", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("ingest-lag")).Outcome);
        Assert.DoesNotContain(host.CompletedRuns, r => r.ConditionName == "reports-export-stuck");

        gate.SetResult();
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("reports-slow")).Outcome);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("reports-export-stuck")).Outcome);
        await PollyControlTests.WaitUntilAsync(() => GlobalSlotsFree(host) == 2 && GroupSlotsFree(host, "reports") == 1);
    }

    [Fact]
    public async Task A_group_limit_of_one_serializes_the_runs_of_the_group()
    {
        var running = 0;
        var most = 0;
        async Task Work()
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            await Task.Delay(150);
            Interlocked.Decrement(ref running);
        }

        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.MaxConcurrentHealings = 4)
            .ConfigureGroup("reports", g => g.MaxConcurrentHealings = 1)
            .AddCondition("reports-a", c => c.ForSource("a").InGroup("reports")
                .Heal(p => p.With("work-a", (_, _) => Work()).ActionTimeout(Forever).UseVerifier<EngineTests.AlwaysHealedVerifier>()))
            .AddCondition("reports-b", c => c.ForSource("b").InGroup("reports")
                .Heal(p => p.With("work-b", (_, _) => Work()).ActionTimeout(Forever).UseVerifier<EngineTests.AlwaysHealedVerifier>()))
            .AddCondition("reports-c", c => c.ForSource("c").InGroup("reports")
                .Heal(p => p.With("work-c", (_, _) => Work()).ActionTimeout(Forever).UseVerifier<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("a", SignalSeverity.Unhealthy);
        host.Publish("b", SignalSeverity.Unhealthy);
        host.Publish("c", SignalSeverity.Unhealthy);
        foreach (var name in new[] { "reports-a", "reports-b", "reports-c" })
        {
            Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync(name)).Outcome);
        }

        Assert.Equal(1, Volatile.Read(ref most));
    }

    [Fact]
    public async Task Runs_canceled_while_waiting_for_a_group_slot_or_a_global_slot_leak_no_slot()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.MaxConcurrentHealings = 1)
            .ConfigureGroup("reports", g => g.MaxConcurrentHealings = 1)
            .AddCondition("ingest-lag", c => c.ForSource("ingest").InGroup("ingest")
                .Heal(p => p.With("hold", async (_, ct) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }).ActionTimeout(Forever)))
            .AddCondition("reports-a", c => c.ForSource("a").InGroup("reports").Heal(p => p.With("work-a", _ => { })))
            .AddCondition("reports-b", c => c.ForSource("b").InGroup("reports").Heal(p => p.With("work-b", _ => { }))));
        await using var owned = host;

        var runner = host.Services.GetRequiredService<Engine.HealingRunner>();
        var reports = host.Services.GetRequiredService<HealingGroups>().Get("reports")!.Concurrency!;

        // ingest holds the only global slot; reports-a takes its group's slot and waits for the global one; reports-b
        // waits for the group's slot.
        host.Publish("ingest", SignalSeverity.Unhealthy);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.Publish("a", SignalSeverity.Unhealthy);
        await PollyControlTests.WaitUntilAsync(() => reports.CurrentCount == 0);
        host.Publish("b", SignalSeverity.Unhealthy);
        await Task.Delay(200);
        Assert.Equal(0, GlobalSlotsFree(runner));

        // The application stops: every wait is canceled, and every slot is given back.
        await host.DisposeAsync();
        await runner.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, GlobalSlotsFree(runner));
        Assert.Equal(1, reports.CurrentCount);
        Assert.DoesNotContain(host.CompletedRuns, r => r.Steps.Count > 0 && r.ConditionName != "ingest-lag");
    }

    [Fact]
    public async Task Both_slots_are_given_back_while_waiting_for_an_approval_and_taken_again_afterwards()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.MaxConcurrentHealings = 1)
            .ConfigureGroup("reports", g => g.MaxConcurrentHealings = 1)
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")
                .Heal(p => p.With("recycle", _ => { }).RequireApproval().UseVerifier<EngineTests.AlwaysHealedVerifier>()))
            .AddCondition("reports-export-stuck", c => c.ForSource("exports").InGroup("reports")
                .Heal(p => p.With("restart-export", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>()))
            .AddCondition("ingest-lag", c => c.ForSource("ingest")
                .Heal(p => p.With("scale-ingest", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("reports", SignalSeverity.Unhealthy);
        var request = await host.WaitForApprovalAsync("reports-slow");

        // While a person decides, the group's slot and the global slot are free.
        host.Publish("exports", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("reports-export-stuck")).Outcome);
        host.Publish("ingest", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("ingest-lag")).Outcome);

        Assert.True(host.Approvals.Approve(request.Id, "on-call"));
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("reports-slow")).Outcome);
        await PollyControlTests.WaitUntilAsync(() => GlobalSlotsFree(host) == 1 && GroupSlotsFree(host, "reports") == 1);
    }

    // ---------------------------------------------------------------- G3: budget, group first then global

    [Fact]
    public async Task An_exhausted_group_budget_stops_only_its_own_conditions_and_the_error_names_the_group()
    {
        var logs = new LogCollector();
        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .ConfigureGroup("reports", g => { g.MaxActionsPerWindow = 1; g.ActionBudgetWindow = TimeSpan.FromHours(1); })
                .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")
                    .Heal(p => p.With("recycle", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>()))
                .AddCondition("reports-export-stuck", c => c.ForSource("exports").InGroup("reports")
                    .Heal(p => p.With("restart-export", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>()))
                .AddCondition("ingest-lag", c => c.ForSource("ingest").InGroup("ingest")
                    .Heal(p => p.With("scale-ingest", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>())),
            services => services.AddSingleton<ILoggerProvider>(logs));

        host.Publish("reports", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("reports-slow")).Outcome);

        host.Publish("exports", SignalSeverity.Unhealthy);
        var refused = await host.WaitForRunAsync("reports-export-stuck");
        Assert.Equal(HealingRunOutcome.BudgetExhausted, refused.Outcome);
        Assert.Empty(refused.Steps);
        Assert.Contains(logs.Messages(LogLevel.Error), m => m.Contains("action budget of group 'reports' exhausted (1 actions per 01:00:00)"));

        // Another group goes on.
        host.Publish("ingest", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("ingest-lag")).Outcome);

        var budget = await host.Control.GetGroupBudgetAsync("reports") ?? throw new InvalidOperationException("no group budget");
        Assert.Equal("reports", budget.Group);
        Assert.Equal(1, budget.Used);
        Assert.True(budget.IsExhausted);
        Assert.Equal(TimeSpan.FromHours(1), budget.Window);
        Assert.Null(await host.Control.GetGroupBudgetAsync("ingest"));
        Assert.Null(await host.Control.GetGroupBudgetAsync("nobody"));
        Assert.Equal(2, (await host.Control.GetBudgetAsync()).Used);
    }

    [Fact]
    public async Task The_global_budget_still_applies_and_a_group_unit_spent_before_it_refused_is_not_given_back()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.MaxActionsPerWindow = 1)
            .ConfigureGroup("reports", g => g.MaxActionsPerWindow = 5)
            .AddCondition("ingest-lag", c => c.ForSource("ingest")
                .Heal(p => p.With("scale-ingest", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>()))
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")
                .Heal(p => p.With("recycle", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("ingest", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("ingest-lag")).Outcome);

        host.Publish("reports", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("reports-slow");
        Assert.Equal(HealingRunOutcome.BudgetExhausted, run.Outcome);
        Assert.Empty(run.Steps);

        // The group's unit was spent first and stays spent: it costs the group only.
        Assert.Equal(1, (await host.Control.GetGroupBudgetAsync("reports"))!.Used);
        Assert.Equal(1, (await host.Control.GetBudgetAsync()).Used);
    }

    [Fact]
    public async Task A_compensation_is_never_blocked_by_an_exhausted_group_budget()
    {
        var undone = 0;
        var second = 0;
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .ConfigureGroup("reports", g => g.MaxActionsPerWindow = 1)
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")
                .Heal(p => p
                    .With("switch-replica", _ => { })
                    .UndoWith("switch-back", (_, _) => { Interlocked.Increment(ref undone); return Task.CompletedTask; })
                    .Then("recycle", _ => Interlocked.Increment(ref second))
                    .UseVerifier<NotHealedVerifier>())));

        host.Publish("reports", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("reports-slow");

        // The first step spent the group's only unit; its undo still ran; the next step was refused.
        Assert.Equal(HealingRunOutcome.BudgetExhausted, run.Outcome);
        Assert.True(Assert.Single(run.Steps).UndoSucceeded);
        Assert.Equal(1, undone);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task A_group_budget_the_store_cannot_check_fails_closed_and_a_hanging_one_ends_with_the_shutdown()
    {
        var store = new GroupGateStore { FailGroupBudget = true };
        var executed = 0;
        var host = await HomeostatTestHost.StartAsync(h => h
            .UseStore(_ => store)
            .ConfigureGroup("reports", g => { g.MaxActionsPerWindow = 3; g.MaxConcurrentHealings = 1; })
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")
                .Heal(p => p.With("recycle", _ => Interlocked.Increment(ref executed)).UseVerifier<EngineTests.AlwaysHealedVerifier>())));
        await using var owned = host;

        host.Publish("reports", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("reports-slow");
        Assert.Equal(HealingRunOutcome.Failed, run.Outcome);
        Assert.Equal(0, executed);
        Assert.Equal(0, (await host.Control.GetBudgetAsync()).Used);

        // A store that never answers: the shutdown ends the wait, and the slots are given back.
        store.FailGroupBudget = false;
        store.HangGroupBudget = true;
        host.Time.Advance(TimeSpan.FromHours(1));
        host.Publish("reports", SignalSeverity.Unhealthy);
        await PollyControlTests.WaitUntilAsync(() => store.GroupBudgetCalls >= 2);
        var runner = host.Services.GetRequiredService<Engine.HealingRunner>();
        var reports = host.Services.GetRequiredService<HealingGroups>().Get("reports")!.Concurrency!;
        await host.DisposeAsync();
        await runner.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HealingRunOutcome.Canceled, host.CompletedRuns[^1].Outcome);
        Assert.Equal(0, executed);
        Assert.Equal(1, GlobalSlotsFree(runner));
        Assert.Equal(1, reports.CurrentCount);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("file")]
    [InlineData("redis")]
    public async Task Group_budgets_are_atomic_separate_per_group_and_kept_by_every_store(string kind)
    {
        using var redis = kind == "redis" ? RedisFixture.Connect() : null;
        var prefix = kind == "redis" ? RedisFixture.Prefix() : null;
        var path = Path.Combine(Path.GetTempPath(), $"homeostat-group-budget-{Guid.NewGuid():N}.json");
        IHomeostatStore Open() => kind switch
        {
            "memory" => new InMemoryHomeostatStore(),
            "file" => new FileHomeostatStore(path),
            _ => CreateRedisStore(redis!, prefix!),
        };

        var store = Open();
        try
        {
            var window = TimeSpan.FromMinutes(10);
            var t0 = DateTimeOffset.Parse("2026-11-04T10:00:00Z");

            // Concurrent consumers never exceed the limit.
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => store.TryConsumeGroupBudgetAsync("reports", t0, 3, window).AsTask()));
            Assert.Equal(3, results.Count(r => r));
            Assert.Equal(3, await store.GetGroupBudgetUsedAsync("reports", t0.AddMinutes(1), window));

            // Each group has its own budget, separate from the global one.
            Assert.True(await store.TryConsumeGroupBudgetAsync("ingest", t0, 3, window));
            Assert.Equal(1, await store.GetGroupBudgetUsedAsync("ingest", t0, window));
            Assert.Equal(0, await store.GetBudgetUsedAsync(t0, window));
            Assert.Equal(0, await store.GetGroupBudgetUsedAsync("nobody", t0, window));

            if (kind == "file")
            {
                // Kept across a restart.
                ((IDisposable)store).Dispose();
                store = Open();
                Assert.Equal(3, await store.GetGroupBudgetUsedAsync("reports", t0.AddMinutes(1), window));
                Assert.False(await store.TryConsumeGroupBudgetAsync("reports", t0.AddMinutes(1), 3, window));
            }

            // The window slides.
            Assert.True(await store.TryConsumeGroupBudgetAsync("reports", t0.AddMinutes(10), 3, window));
            Assert.Equal(1, await store.GetGroupBudgetUsedAsync("reports", t0.AddMinutes(10), window));
        }
        finally
        {
            (store as IDisposable)?.Dispose();
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    // ---------------------------------------------------------------- G4: pause per group

    [Fact]
    public async Task A_paused_group_does_not_start_its_runs_while_other_groups_heal_and_resuming_restores_it()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")
                .Heal(p => p.With("recycle", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>()))
            .AddCondition("ingest-lag", c => c.ForSource("ingest").InGroup("ingest")
                .Heal(p => p.With("scale-ingest", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>())));

        await host.Control.PauseGroupAsync("reports", by: "on-call");
        Assert.True(host.Control.IsGroupPaused("reports"));
        Assert.False(host.Control.IsGroupPaused("ingest"));
        Assert.False(host.Control.IsPaused);
        Assert.Equal("paused by on-call", host.Control.GetGroupPauseReason("reports"));
        Assert.Null(host.Control.GetGroupPauseReason("ingest"));
        var status = host.Control.GetGroups().Single(g => g.Name == "reports");
        Assert.True(status.IsPaused);
        Assert.Equal("on-call", status.Pause!.By);

        host.Publish("reports", SignalSeverity.Unhealthy);
        host.Publish("ingest", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("ingest-lag")).Outcome);
        await host.WaitForConditionAsync("reports-slow", ConditionStatus.Active);
        await Task.Delay(200);
        Assert.DoesNotContain(host.CompletedRuns, r => r.ConditionName == "reports-slow");

        await host.Control.ResumeGroupAsync("reports", "on-call");
        Assert.False(host.Control.IsGroupPaused("reports"));
        Assert.Null(host.Control.GetGroups().Single(g => g.Name == "reports").Pause);
        host.Publish("reports", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("reports-slow")).Outcome);
    }

    [Fact]
    public async Task A_run_stops_before_its_next_step_when_its_group_is_paused_and_its_compensation_still_runs()
    {
        var undone = 0;
        var second = 0;
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")
                .Heal(p => p
                    .With("switch-replica", _ => { })
                    .UndoWith("switch-back", (_, _) => { Interlocked.Increment(ref undone); return Task.CompletedTask; })
                    .Then("recycle", _ => Interlocked.Increment(ref second))
                    .UseVerifier<GroupPausingVerifier>()))
            .AddCondition("ingest-lag", c => c.ForSource("ingest").InGroup("ingest")
                .Heal(p => p.With("scale-ingest", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("reports", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("reports-slow");

        Assert.Equal(HealingRunOutcome.Paused, run.Outcome);
        Assert.True(Assert.Single(run.Steps).UndoSucceeded);
        Assert.Equal(1, undone);
        Assert.Equal(0, second);

        // The conditions of the group are still tracked; the other groups still heal.
        Assert.Equal(ConditionStatus.Active, host.Control.GetConditions().Single(c => c.Name == "reports-slow").Status);
        host.Publish("ingest", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("ingest-lag")).Outcome);
    }

    [Fact]
    public async Task The_global_pause_still_pauses_every_group_and_a_timed_group_pause_ends_by_itself()
    {
        await using var host = await HomeostatTestHost.StartAsync(
            h => h.AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")),
            options: new HomeostatTestHostOptions { AutoAdvanceStep = null });

        await host.Control.PauseAsync(by: "deploy");
        Assert.True(host.Control.IsGroupPaused("reports"));
        Assert.True(host.Control.IsGroupPaused("anything"));
        Assert.Equal("paused by deploy", host.Control.GetGroupPauseReason("reports"));
        Assert.False(host.Control.GetGroups().Single().IsPaused);   // the group itself is not paused

        // Decided after the pause (on a tie, the pause would win).
        host.Time.Advance(TimeSpan.FromSeconds(1));
        await host.Control.ResumeAsync();
        Assert.False(host.Control.IsGroupPaused("reports"));

        var until = host.Time.GetUtcNow().AddMinutes(1);
        await host.Control.PauseGroupAsync("reports", until, "on-call");
        Assert.True(host.Control.IsGroupPaused("reports"));
        Assert.Equal($"paused until {until:O} by on-call", host.Control.GetGroupPauseReason("reports"));

        host.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.False(host.Control.IsGroupPaused("reports"));
        Assert.Null(host.Control.GetGroupPauseReason("reports"));
        var status = host.Control.GetGroups().Single();
        Assert.False(status.IsPaused);
        Assert.Equal(until, status.Pause!.Until);   // listed until resumed, no longer in force
    }

    [Fact]
    public async Task A_group_counts_as_paused_until_the_control_state_is_read()
    {
        var store = new InMemoryHomeostatStore();
        await store.TrySetGroupPauseAsync("reports", new PauseState { At = DateTimeOffset.UtcNow, By = "on-call" }, DateTimeOffset.UtcNow);
        var services = new ServiceCollection().AddLogging();
        services.AddHomeostat(h => h.UseStore(_ => store).AddCondition("ingest-lag", c => c.ForSource("ingest").InGroup("ingest")));
        await using var provider = services.BuildServiceProvider();
        var control = provider.GetRequiredService<IHomeostatControl>();

        Assert.True(control.IsGroupPaused("ingest"));
        Assert.StartsWith("waiting for the control state", control.GetGroupPauseReason("ingest"));
        Assert.True(control.GetGroups().Single().IsPaused);

        await provider.GetRequiredService<Engine.SharedControl>().TryRefreshAsync(CancellationToken.None);
        Assert.False(control.IsGroupPaused("ingest"));
        Assert.Equal("paused by on-call", control.GetGroupPauseReason("reports"));
        Assert.Contains(control.GetGroups(), g => g.Name == "reports" && g.IsPaused);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("file")]
    [InlineData("redis")]
    public async Task Group_pauses_are_written_in_decision_order_per_group_by_every_store(string kind)
    {
        using var redis = kind == "redis" ? RedisFixture.Connect() : null;
        var prefix = kind == "redis" ? RedisFixture.Prefix() : null;
        var path = Path.Combine(Path.GetTempPath(), $"homeostat-group-pause-{Guid.NewGuid():N}.json");
        IHomeostatStore Open() => kind switch
        {
            "memory" => new InMemoryHomeostatStore(),
            "file" => new FileHomeostatStore(path),
            _ => CreateRedisStore(redis!, prefix!),
        };

        var store = Open();
        try
        {
            var t0 = DateTimeOffset.Parse("2026-11-04T10:00:00Z");
            PauseState Pause(double seconds, string? by = null) => new() { At = t0.AddSeconds(seconds), By = by };
            async Task<GroupPauseState?> Read(string group) => (await store.GetControlStateAsync()).GroupPauses.SingleOrDefault(p => p.Group == group);

            // A resume decided after a pause, then the pause written late: the resume stands.
            Assert.True(await store.TrySetGroupPauseAsync("reports", null, t0.AddSeconds(2)));
            Assert.False(await store.TrySetGroupPauseAsync("reports", Pause(1, "deploy"), t0.AddSeconds(1)));
            Assert.Null(await Read("reports"));

            // A newer pause applies; a resume decided before it, written late, does not end it.
            Assert.True(await store.TrySetGroupPauseAsync("reports", Pause(3, "on-call"), t0.AddSeconds(3)));
            Assert.False(await store.TrySetGroupPauseAsync("reports", null, t0.AddSeconds(2.5)));
            Assert.Equal("on-call", (await Read("reports"))!.By);

            // On a tie the pause wins: a resume stamped like the latest decision is refused, a pause written again is not.
            Assert.False(await store.TrySetGroupPauseAsync("reports", null, t0.AddSeconds(3)));
            Assert.True(await store.TrySetGroupPauseAsync("reports", Pause(3, "on-call"), t0.AddSeconds(3)));

            // Each group, and the global pause, is decided on its own.
            Assert.True(await store.TrySetGroupPauseAsync("ingest", Pause(1), t0.AddSeconds(1)));
            Assert.Null((await store.GetControlStateAsync()).Pause);
            Assert.True(await store.TrySetPauseAsync(null, t0.AddSeconds(10)));
            Assert.NotNull(await Read("reports"));
            Assert.True(await store.TrySetGroupPauseAsync("ingest", null, t0.AddSeconds(4)));
            Assert.Null(await Read("ingest"));
            Assert.Equal(["reports"], (await store.GetControlStateAsync()).GroupPauses.Select(p => p.Group));

            if (kind == "file")
            {
                ((IDisposable)store).Dispose();
                store = Open();
                Assert.Equal("on-call", (await Read("reports"))!.By);
            }

            // Bounded: one group too many is refused; replacing a pause, and pausing after a resume, are not.
            for (var i = 1; i < ControlState.MaxGroupPauses; i++)
            {
                Assert.True(await store.TrySetGroupPauseAsync($"group-{i:000}", Pause(20), t0.AddSeconds(20)));
            }

            Assert.Equal(ControlState.MaxGroupPauses, (await store.GetControlStateAsync()).GroupPauses.Count);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.TrySetGroupPauseAsync("one-too-many", Pause(21), t0.AddSeconds(21)).AsTask());
            Assert.True(await store.TrySetGroupPauseAsync("reports", Pause(22, "again"), t0.AddSeconds(22)));
            Assert.True(await store.TrySetGroupPauseAsync("group-001", null, t0.AddSeconds(23)));
            Assert.True(await store.TrySetGroupPauseAsync("one-too-many", Pause(24), t0.AddSeconds(24)));
        }
        finally
        {
            (store as IDisposable)?.Dispose();
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task An_unreadable_Redis_group_pause_record_counts_as_a_pause_of_its_group()
    {
        using var redis = RedisFixture.Connect();
        var prefix = RedisFixture.Prefix();
        var store = CreateRedisStore(redis, prefix);
        await redis.GetDatabase().HashSetAsync($"{prefix}{{control}}:group-pauses", "reports", "{not json");

        var pause = Assert.Single((await store.GetControlStateAsync()).GroupPauses);
        Assert.Equal("reports", pause.Group);
        Assert.Null(pause.Until);

        // A person resumes on purpose, which replaces the record.
        Assert.True(await store.TrySetGroupPauseAsync("reports", null, DateTimeOffset.UtcNow));
        Assert.Empty((await store.GetControlStateAsync()).GroupPauses);
    }

    [Fact]
    public async Task Two_instances_sharing_a_store_apply_group_pauses_and_resumes_in_decision_order()
    {
        var store = new GroupGateStore();
        await using var a = await StartInstanceAsync("a", store);
        await using var b = await StartInstanceAsync("b", store);

        // A pause made on one instance reaches the other at its next read.
        await a.Control.PauseGroupAsync("reports", by: "on-call");
        Assert.True(a.Control.IsGroupPaused("reports"));
        await RefreshAsync(b);
        Assert.True(b.Control.IsGroupPaused("reports"));
        Assert.False(b.Control.IsGroupPaused("ingest"));
        Assert.False(b.Control.IsPaused);

        // A resume decided later on the other instance reaches the first one.
        Advance(TimeSpan.FromSeconds(1), a, b);
        await b.Control.ResumeGroupAsync("reports", "on-call");
        Assert.False(b.Control.IsGroupPaused("reports"));
        await RefreshAsync(a);
        Assert.False(a.Control.IsGroupPaused("reports"));

        // b decides a resume (its write is slow); a pause decided after it on a reaches the store first: the late resume
        // is refused, and both instances stay paused.
        await a.Control.PauseGroupAsync("reports", by: "deploy");
        await RefreshAsync(b);
        Advance(TimeSpan.FromSeconds(1), a, b);
        store.HoldGroupResumes = true;
        var resuming = b.Control.ResumeGroupAsync("reports", "on-call").AsTask();
        await PollyControlTests.WaitUntilAsync(() => store.GroupResumeWaiting);
        Advance(TimeSpan.FromSeconds(1), a);
        await a.Control.PauseGroupAsync("reports", by: "deploy again");
        store.Release.Set();
        await resuming;

        await RefreshAsync(a);
        await RefreshAsync(b);
        Assert.Equal("deploy again", (await store.GetControlStateAsync()).GroupPauses.Single().By);
        Assert.True(a.Control.IsGroupPaused("reports"));
        Assert.True(b.Control.IsGroupPaused("reports"));
        Assert.Equal("paused by deploy again", b.Control.GetGroupPauseReason("reports"));
    }

    [Fact]
    public async Task A_group_resume_that_times_out_leaves_the_group_paused_even_when_this_instance_did_not_show_the_pause()
    {
        var store = new GroupGateStore();
        await using var host = await StartInstanceAsync("b", store);

        // Paused on another instance: this one has not read it yet.
        await store.Inner.TrySetGroupPauseAsync("reports", new PauseState { By = "deploy", At = host.Time.GetUtcNow() }, host.Time.GetUtcNow());
        host.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(host.Control.IsGroupPaused("reports"));

        // The resume reaches a slow store and the caller gives up (as at the write timeout).
        store.HoldGroupResumes = true;
        using var giveUp = new CancellationTokenSource();
        var resuming = host.Control.ResumeGroupAsync("reports", "on-call", giveUp.Token).AsTask();
        await PollyControlTests.WaitUntilAsync(() => store.GroupResumeWaiting);
        giveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resuming);
        Assert.True(host.Control.IsGroupPaused("reports"));

        // The store then runs the late resume: the pause decided again after it stands, as the caller was told.
        store.Release.Set();
        await store.GroupResumeDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var pause = Assert.Single((await store.GetControlStateAsync()).GroupPauses);
        Assert.Equal(Homeostat.HomeostatControl.TimedOutResume, pause.By);
        await RefreshAsync(host);
        Assert.True(host.Control.IsGroupPaused("reports"));
        Assert.False(host.Control.IsPaused);
    }

    [Fact]
    public async Task With_the_store_down_a_group_pause_applies_at_once_and_is_written_later_while_a_group_resume_fails()
    {
        var store = new GroupGateStore();
        await using var host = await StartInstanceAsync("a", store);

        store.Down = true;
        await host.Control.PauseGroupAsync("reports", by: "on-call");
        Assert.True(host.Control.IsGroupPaused("reports"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Control.ResumeGroupAsync("reports").AsTask());
        Assert.True(host.Control.IsGroupPaused("reports"));
        Assert.Empty((await store.Inner.GetControlStateAsync()).GroupPauses);

        // The store recovers: the pause is written at the next refresh.
        store.Down = false;
        await RefreshAsync(host);
        Assert.Equal("on-call", Assert.Single((await store.Inner.GetControlStateAsync()).GroupPauses).By);
        Assert.True(host.Control.IsGroupPaused("reports"));
    }

    [Fact]
    public async Task At_most_256_groups_can_be_paused_at_once()
    {
        await using var host = await HomeostatTestHost.StartAsync(_ => { }, options: new HomeostatTestHostOptions { AutoAdvanceStep = null });
        for (var i = 0; i < ControlState.MaxGroupPauses; i++)
        {
            await host.Control.PauseGroupAsync($"group-{i:000}");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Control.PauseGroupAsync("one-too-many").AsTask());
        Assert.False(host.Control.IsGroupPaused("one-too-many"));

        // Replacing a pause is allowed; a resume makes room.
        await host.Control.PauseGroupAsync("group-000", by: "again");
        host.Time.Advance(TimeSpan.FromSeconds(1));
        await host.Control.ResumeGroupAsync("group-001");
        await host.Control.PauseGroupAsync("one-too-many");
        Assert.True(host.Control.IsGroupPaused("one-too-many"));
        Assert.Throws<ArgumentException>(() => { _ = host.Control.PauseGroupAsync("bad\nname"); });
    }

    // ---------------------------------------------------------------- G5: maintenance windows per group

    [Fact]
    public async Task A_maintenance_window_for_groups_pauses_only_those_groups()
    {
        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .AddMaintenanceWindow("reports-nightly", w => w.Daily(new TimeOnly(0, 0), TimeSpan.FromHours(2)).ForGroups("reports", "exports"))
                .AddCondition("reports-slow", c => c.ForSource("reports").InGroup("reports")
                    .Heal(p => p.With("recycle", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>()))
                .AddCondition("ingest-lag", c => c.ForSource("ingest").InGroup("ingest")
                    .Heal(p => p.With("scale-ingest", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>()))
                .AddCondition("feed-down", c => c.ForSource("feed")
                    .Heal(p => p.With("renew", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>())),
            options: new HomeostatTestHostOptions { AutoAdvanceStep = null });

        var window = host.Services.GetServices<MaintenanceWindow>().Single();
        Assert.Equal(["reports", "exports"], window.Groups);

        Assert.False(host.Control.IsPaused);
        Assert.Null(host.Control.PauseReason);
        Assert.True(host.Control.IsGroupPaused("reports"));
        Assert.True(host.Control.IsGroupPaused("exports"));
        Assert.False(host.Control.IsGroupPaused("ingest"));
        Assert.Equal("maintenance window 'reports-nightly'", host.Control.GetGroupPauseReason("reports"));
        Assert.True(host.Control.GetGroups().Single(g => g.Name == "reports").IsPaused);

        host.Publish("reports", SignalSeverity.Unhealthy);
        host.Publish("ingest", SignalSeverity.Unhealthy);
        host.Publish("feed", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("ingest-lag")).Outcome);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("feed-down")).Outcome);
        Assert.DoesNotContain(host.CompletedRuns, r => r.ConditionName == "reports-slow");

        // Once the window closes, the group heals again.
        host.Time.Advance(TimeSpan.FromHours(2));
        Assert.False(host.Control.IsGroupPaused("reports"));
        host.Publish("reports", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("reports-slow")).Outcome);
    }

    [Fact]
    public void The_documentation_states_the_group_limits_the_code_uses()
    {
        string Text(string file)
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Homeostat.slnx")))
                {
                    var text = File.ReadAllText(Path.Combine(directory.FullName, file)).ReplaceLineEndings(" ");
                    while (text.Contains("  ", StringComparison.Ordinal))
                    {
                        text = text.Replace("  ", " ", StringComparison.Ordinal);
                    }

                    return text;
                }
            }

            throw new InvalidOperationException("The repository root (Homeostat.slnx) was not found.");
        }

        Assert.Contains($"At most {ControlState.MaxGroupPauses} groups can be paused at once", Text("docs/guardrails.md"));
        Assert.Contains($"at most {HealingGroupNames.MaxLength} characters", Text("docs/guardrails.md"));
        Assert.Contains($"at most {HealingGroupNames.MaxLength} characters", Text("docs/conditions.md"));
        Assert.Contains($"holds at most {ControlState.MaxGroupPauses} of them", Text("docs/persistence-and-clusters.md"));
    }

    // ---------------------------------------------------------------- helpers

    private static Task<HomeostatTestHost> StartInstanceAsync(string instance, IHomeostatStore store, Action<HomeostatBuilder>? configure = null)
        => HomeostatTestHost.StartAsync(
            h =>
            {
                h.UseStore(_ => store).Configure(o =>
                {
                    o.InstanceId = instance;
                    o.ControlRefreshInterval = TimeSpan.FromSeconds(5);
                });
                configure?.Invoke(h);
            },
            options: new HomeostatTestHostOptions { AutoAdvanceStep = null });

    // Twice: a refresh may join a read that started before the change being checked (decision P8).
    private static async Task RefreshAsync(HomeostatTestHost host)
    {
        var control = host.Services.GetRequiredService<Engine.SharedControl>();
        await control.TryRefreshAsync(CancellationToken.None);
        await control.TryRefreshAsync(CancellationToken.None);
    }

    private static void Advance(TimeSpan by, params HomeostatTestHost[] hosts)
    {
        foreach (var host in hosts)
        {
            host.Time.Advance(by);
        }
    }

    private static readonly FieldInfo GlobalSlots = typeof(Engine.HealingRunner).GetField("_concurrency", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static int GlobalSlotsFree(Engine.HealingRunner runner) => ((SemaphoreSlim)GlobalSlots.GetValue(runner)!).CurrentCount;

    private static int GlobalSlotsFree(HomeostatTestHost host) => GlobalSlotsFree(host.Services.GetRequiredService<Engine.HealingRunner>());

    private static int GroupSlotsFree(HomeostatTestHost host, string group)
        => host.Services.GetRequiredService<HealingGroups>().Get(group)!.Concurrency!.CurrentCount;

    private static RedisHomeostatStore CreateRedisStore(IConnectionMultiplexer redis, string prefix)
        => new(redis, Options.Create(new RedisStoreOptions { KeyPrefix = prefix }), Options.Create(new HomeostatOptions()));

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    internal sealed class NotHealedVerifier : IHealingVerifier
    {
        public Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(VerificationOutcome.NotHealed);
    }

    /// <summary>An operator pauses the group while the step is being verified, and the step did not help.</summary>
    internal sealed class GroupPausingVerifier(IHomeostatControl control) : IHealingVerifier
    {
        public async Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await control.PauseGroupAsync("reports", by: "on-call", cancellationToken: cancellationToken);
            return VerificationOutcome.NotHealed;
        }
    }

    /// <summary>Collects log entries with the scopes they were written in.</summary>
    private sealed class LogCollector : ILoggerProvider
    {
        private readonly AsyncLocal<IReadOnlyList<KeyValuePair<string, object?>>?> _scope = new();

        public ConcurrentQueue<(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> Scope)> Entries { get; } = new();

        public IReadOnlyList<string> Messages(LogLevel level) => Entries.Where(e => e.Level == level).Select(e => e.Message).ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(LogCollector owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                var outer = owner._scope.Value;
                var added = state is IEnumerable<KeyValuePair<string, object?>> pairs ? pairs.ToArray() : [];
                owner._scope.Value = [.. outer ?? [], .. added];
                return new Restore(owner, outer);
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Entries.Enqueue((logLevel, formatter(state, exception), owner._scope.Value ?? []));
        }

        private sealed class Restore(LogCollector owner, IReadOnlyList<KeyValuePair<string, object?>>? outer) : IDisposable
        {
            public void Dispose() => owner._scope.Value = outer;
        }
    }

    /// <summary>An in-memory store (with decision order) that can be down, hold group resumes, or fail or hang group budgets.</summary>
    private sealed class GroupGateStore : IHomeostatStore
    {
        public InMemoryHomeostatStore Inner { get; } = new();

        public volatile bool Down;

        public volatile bool HoldGroupResumes;

        public volatile bool GroupResumeWaiting;

        public volatile bool FailGroupBudget;

        public volatile bool HangGroupBudget;

        private int _groupBudgetCalls;

        public int GroupBudgetCalls => Volatile.Read(ref _groupBudgetCalls);

        public ManualResetEventSlim Release { get; } = new(false);

        public TaskCompletionSource GroupResumeDone { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private void Check()
        {
            if (Down)
            {
                throw new InvalidOperationException("store down");
            }
        }

        public async ValueTask<bool> TrySetGroupPauseAsync(string group, PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
        {
            Check();
            if (pause is not null || !HoldGroupResumes)
            {
                return await Inner.TrySetGroupPauseAsync(group, pause, decidedAt, cancellationToken);
            }

            GroupResumeWaiting = true;
            await Task.Run(() => Release.Wait(TimeSpan.FromSeconds(20)), CancellationToken.None);
            try
            {
                return await Inner.TrySetGroupPauseAsync(group, null, decidedAt, CancellationToken.None);
            }
            finally
            {
                GroupResumeDone.TrySetResult();
            }
        }

        public async ValueTask<bool> TryConsumeGroupBudgetAsync(string group, DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default)
        {
            Check();
            Interlocked.Increment(ref _groupBudgetCalls);
            if (FailGroupBudget)
            {
                throw new InvalidOperationException("store down");
            }

            if (HangGroupBudget)
            {
                await new TaskCompletionSource().Task;
            }

            return await Inner.TryConsumeGroupBudgetAsync(group, now, limit, window, cancellationToken);
        }

        public ValueTask<int> GetGroupBudgetUsedAsync(string group, DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) { Check(); return Inner.GetGroupBudgetUsedAsync(group, now, window, cancellationToken); }

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) { Check(); return Inner.GetPlanStateAsync(key, cancellationToken); }

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) { Check(); return Inner.UpdatePlanStateAsync(key, update, cancellationToken); }

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) { Check(); return Inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken); }

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) { Check(); return Inner.GetBudgetUsedAsync(now, window, cancellationToken); }

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) { Check(); return Inner.AppendRunAsync(run, maxRuns, cancellationToken); }

        public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) { Check(); return Inner.GetRecentRunsAsync(maxRuns, cancellationToken); }

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) { Check(); return Inner.GetControlStateAsync(cancellationToken); }

        public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Check(); return Inner.TrySetPauseAsync(pause, decidedAt, cancellationToken); }

        public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Check(); return Inner.TrySetSwitchAsync(name, state, decidedAt, cancellationToken); }

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) { Check(); return Inner.TryClaimSwitchAsync(state, cancellationToken); }

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) { Check(); return Inner.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken); }

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) { Check(); return Inner.AddApprovalAsync(approval, cancellationToken); }

        public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default) { Check(); return Inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken); }

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) { Check(); return Inner.RemoveApprovalAsync(id, cancellationToken); }
    }
}
