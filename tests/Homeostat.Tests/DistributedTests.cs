using Homeostat.Coordination;
using Homeostat.Healing;
using Homeostat.Persistence;
using Homeostat.Redis;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Homeostat.Tests;

/// <summary>Stores, coordination across instances, backoff, compensation and maintenance windows.</summary>
public class DistributedTests
{
    private static readonly DateTimeOffset T0 = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- several instances, in process

    [Fact]
    public async Task A_cluster_plan_runs_once_across_instances_sharing_store_and_coordinator()
    {
        var store = new InMemoryHomeostatStore();
        var coordinator = new InMemoryCoordinator();
        var recorder = new EngineTests.Recorder();

        await using var a = await StartInstanceAsync("a", recorder, h => h.UseStore(_ => store).UseCoordinator(_ => coordinator));
        await using var b = await StartInstanceAsync("b", recorder, h => h.UseStore(_ => store).UseCoordinator(_ => coordinator));

        a.Publish("feed", SignalSeverity.Unhealthy);
        b.Publish("feed", SignalSeverity.Unhealthy);

        var run = await WaitForAnyRunAsync(a, b);
        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        await Task.Delay(300);

        Assert.Equal(1, recorder.Executions);
        var record = Assert.Single(await a.Control.GetRecentRunsAsync());
        Assert.Contains(record.Instance, new[] { "a", "b" });
        Assert.NotNull((await store.GetPlanStateAsync("feed-down")).LastRunEndedAt);
    }

    [Fact]
    public async Task Instance_plans_keep_separate_cooldowns_in_a_shared_store()
    {
        var store = new InMemoryHomeostatStore();
        var recorder = new EngineTests.Recorder();

        await using var a = await StartInstanceAsync("a", recorder, h => h.UseStore(_ => store), cluster: false);
        await using var b = await StartInstanceAsync("b", recorder, h => h.UseStore(_ => store), cluster: false);

        a.Publish("feed", SignalSeverity.Unhealthy);
        await a.WaitForRunAsync("feed-down");
        b.Publish("feed", SignalSeverity.Unhealthy);
        await b.WaitForRunAsync("feed-down");

        Assert.Equal(2, recorder.Executions);
        Assert.NotNull((await store.GetPlanStateAsync("a/feed-down")).LastRunEndedAt);
        Assert.NotNull((await store.GetPlanStateAsync("b/feed-down")).LastRunEndedAt);
    }

    // ---------------------------------------------------------------- file store

    [Fact]
    public async Task File_store_keeps_cooldown_and_history_across_restarts()
    {
        var path = Path.Combine(Path.GetTempPath(), "homeostat-tests", Guid.NewGuid().ToString("N"), "state.json");
        var recorder = new EngineTests.Recorder();

        await using (var first = await StartInstanceAsync("a", recorder, h => h.UseFileStore(path), cluster: false))
        {
            first.Publish("feed", SignalSeverity.Unhealthy);
            await first.WaitForRunAsync("feed-down");
        }

        await using (var second = await StartInstanceAsync("a", recorder, h => h.UseFileStore(path), cluster: false))
        {
            Assert.Single(await second.Control.GetRecentRunsAsync());
            Assert.Equal(1, (await second.Control.GetBudgetAsync()).Used);

            // Same simulated start time: the cooldown of the first run still applies after the restart.
            second.Publish("feed", SignalSeverity.Unhealthy);
            await second.WaitForConditionAsync("feed-down", Conditions.ConditionStatus.Active);
            await Task.Delay(200);
            Assert.Empty(second.CompletedRuns);
        }

        Assert.Equal(1, recorder.Executions);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    // ---------------------------------------------------------------- backoff and compensation

    [Fact]
    public async Task Cooldown_grows_after_runs_that_do_not_heal()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p
                .With("renew", _ => { })
                .UseVerifier<NeverHealedVerifier>()
                .Cooldown(TimeSpan.FromMinutes(1))
                .CooldownBackoff(2, TimeSpan.FromMinutes(3)))));

        // Keep the problem visible: one unhealthy signal every 10 simulated seconds.
        using var timer = host.Time.CreateTimer(_ => host.Publish("feed", SignalSeverity.Unhealthy), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));

        var run1 = await host.WaitForRunAsync("feed-down", TimeSpan.FromSeconds(30));
        var run2 = await host.WaitForRunAsync("feed-down", TimeSpan.FromSeconds(30));
        var run3 = await host.WaitForRunAsync("feed-down", TimeSpan.FromSeconds(30));
        var run4 = await host.WaitForRunAsync("feed-down", TimeSpan.FromSeconds(30));

        static TimeSpan Gap(HealingRunResult before, HealingRunResult after) => after.StartedAt - before.CompletedAt;

        Assert.InRange(Gap(run1, run2), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2.5));   // 1 min × 2
        Assert.InRange(Gap(run2, run3), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3.5));   // 4 min, capped at 3
        Assert.InRange(Gap(run3, run4), TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3.5));
    }

    [Fact]
    public async Task A_step_that_does_not_heal_is_compensated_before_escalating()
    {
        var undone = 0;
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p
                .With("switch-to-backup", _ => { })
                .UndoWith("switch-to-primary", (_, _) => { Interlocked.Increment(ref undone); return Task.CompletedTask; })
                .Then("renew", _ => { })
                .UseVerifier<EngineTests.HealedOnSecondStepVerifier>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Equal(1, Volatile.Read(ref undone));
        Assert.Collection(
            run.Steps,
            s => { Assert.Equal(VerificationOutcome.NotHealed, s.Verification); Assert.True(s.UndoSucceeded); },
            s => { Assert.Equal(VerificationOutcome.Healed, s.Verification); Assert.Null(s.UndoSucceeded); });
    }

    [Fact]
    public void UndoWith_needs_a_previous_step()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHomeostat(h => h
            .AddCondition("x", c => c.ForSource("x").Heal(p => p.UndoWith("undo", (_, _) => Task.CompletedTask)))));
    }

    // ---------------------------------------------------------------- pauses and maintenance windows

    [Fact]
    public async Task Maintenance_window_pauses_healing()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddMaintenanceWindow("nightly", w => w.Daily(new TimeOnly(23, 30), TimeSpan.FromHours(1)))
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p.With<EngineTests.RecordingAction>())),
            services => services.AddSingleton<EngineTests.Recorder>());

        Assert.True(host.Control.IsPaused);
        Assert.Equal("maintenance window 'nightly'", host.Control.PauseReason);

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForConditionAsync("feed-down", Conditions.ConditionStatus.Active);
        await Task.Delay(100);
        Assert.Empty(host.CompletedRuns);
    }

    [Theory]
    [InlineData("2030-01-01T23:45:00Z", true)]   // Tuesday, inside
    [InlineData("2030-01-02T00:15:00Z", true)]   // crosses midnight
    [InlineData("2030-01-02T00:30:00Z", false)]  // just after
    [InlineData("2030-01-02T23:45:00Z", false)]  // Wednesday: not a window day
    public void Maintenance_window_schedule(string at, bool active)
    {
        var builder = new ServiceCollection().AddHomeostat(h => h.AddMaintenanceWindow("w", w => w
            .Daily(new TimeOnly(23, 30), TimeSpan.FromHours(1))
            .On(DayOfWeek.Tuesday)));
        var window = builder.Services.BuildServiceProvider().GetRequiredService<Guardrails.MaintenanceWindow>();

        Assert.Equal(active, window.IsActive(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task PauseUntil_resumes_by_itself()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddCondition("feed-down", c => c.ForSource("feed")));

        host.Control.PauseUntil(host.Time.GetUtcNow().AddMinutes(1));
        Assert.True(host.Control.IsPaused);
        Assert.StartsWith("paused until", host.Control.PauseReason);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (host.Control.IsPaused)
        {
            await Task.Delay(10, cts.Token);
        }

        Assert.Null(host.Control.PauseReason);
    }

    // ---------------------------------------------------------------- Redis

    [Fact]
    public async Task Redis_store_round_trips_plan_state_history_and_budget()
    {
        using var redis = RedisFixture.Connect();
        var store = CreateRedisStore(redis, RedisFixture.Prefix());

        Assert.Equal(PlanState.Empty, await store.GetPlanStateAsync("feed-down"));
        var state = await store.UpdatePlanStateAsync("feed-down", new PlanStateUpdate { LastRunEndedAt = T0, LastRunBy = "a", Failures = FailureCountChange.Increment, Held = true });
        Assert.Equal(new PlanState { LastRunEndedAt = T0, ConsecutiveFailures = 1, Held = true, LastRunBy = "a" }, state);
        Assert.Equal(state, await store.GetPlanStateAsync("feed-down"));

        // Fields left null are kept; the precondition protects another instance's state.
        Assert.Equal(state, await store.UpdatePlanStateAsync("feed-down", new PlanStateUpdate { Held = false, OnlyIfLastRunBy = "b" }));
        var reset = await store.UpdatePlanStateAsync("feed-down", new PlanStateUpdate { Held = false, Failures = FailureCountChange.Reset, OnlyIfLastRunBy = "a" });
        Assert.Equal(state with { Held = false, ConsecutiveFailures = 0 }, reset);

        for (var i = 0; i < 4; i++)
        {
            await store.AppendRunAsync(
                new HealingRunRecord
                {
                    ConditionName = $"c{i}",
                    Outcome = HealingRunOutcome.Healed,
                    StartedAt = T0,
                    CompletedAt = T0.AddSeconds(1),
                    Steps = [new HealingStepRecord { Name = "renew", Succeeded = true, Verification = VerificationOutcome.Healed }],
                    Instance = "a",
                },
                maxRuns: 3);
        }

        var runs = await store.GetRecentRunsAsync(10);
        Assert.Equal(new[] { "c3", "c2", "c1" }, runs.Select(r => r.ConditionName).ToArray());
        Assert.Equal("renew", runs[0].Steps.Single().Name);

        var window = TimeSpan.FromMinutes(10);
        Assert.True(await store.TryConsumeBudgetAsync(T0, 2, window));
        Assert.True(await store.TryConsumeBudgetAsync(T0.AddMinutes(1), 2, window));
        Assert.False(await store.TryConsumeBudgetAsync(T0.AddMinutes(2), 2, window));
        Assert.Equal(2, await store.GetBudgetUsedAsync(T0.AddMinutes(2), window));
        Assert.True(await store.TryConsumeBudgetAsync(T0.AddMinutes(10), 2, window));
    }

    [Fact]
    public async Task Plan_state_updates_are_atomic()
    {
        using var redis = RedisFixture.Connect();
        var prefix = RedisFixture.Prefix();
        IHomeostatStore[] stores = [new InMemoryHomeostatStore(), .. Enumerable.Range(0, 4).Select(_ => CreateRedisStore(redis, prefix))];

        foreach (var group in new[] { stores.Take(1).ToArray(), stores.Skip(1).ToArray() })
        {
            await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
                group[i % group.Length].UpdatePlanStateAsync("plan", new PlanStateUpdate { Failures = FailureCountChange.Increment }).AsTask()));
            Assert.Equal(50, (await group[0].GetPlanStateAsync("plan")).ConsecutiveFailures);
        }
    }

    [Fact]
    public async Task A_plan_never_runs_twice_at_once_on_an_instance()
    {
        var active = 0;
        var maximum = 0;
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o =>
            {
                o.DefaultCooldown = TimeSpan.Zero;
                o.MaxConcurrentHealings = 4;
                o.MaxActionsPerWindow = 1000;
            })
            .AddCondition("feed-down", c => c
                .ForSource("feed")
                .Heal(p => p
                    .With("slow", async (_, _) =>
                    {
                        var now = Interlocked.Increment(ref active);
                        InterlockedMax(ref maximum, now);
                        await Task.Delay(5);
                        Interlocked.Decrement(ref active);
                    })
                    .UseVerifier<EngineTests.AlwaysHealedVerifier>())));

        // Keep triggering signals coming while runs start and end.
        var until = DateTime.UtcNow.AddMilliseconds(800);
        while (DateTime.UtcNow < until)
        {
            host.Publish("feed", SignalSeverity.Unhealthy);
            await Task.Delay(1);
        }

        await Task.Delay(200);
        Assert.True(host.CompletedRuns.Count > 3, $"only {host.CompletedRuns.Count} runs");
        Assert.Equal(1, Volatile.Read(ref maximum));
    }

    [Fact]
    public async Task A_cluster_run_stops_when_its_lease_is_lost()
    {
        var coordinator = new LosableCoordinator();
        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .UseCoordinator(_ => coordinator)
                .AddCondition("feed-down", c => c
                    .ForSource("feed")
                    .Heal(p => p
                        .With("first", _ => { }).RequireApproval()
                        .OncePerCluster())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var request = await host.WaitForApprovalAsync("feed-down");
        coordinator.LoseAll();
        host.Approvals.Approve(request.Id);

        var run = await host.WaitForRunAsync("feed-down");
        Assert.Equal(HealingRunOutcome.Canceled, run.Outcome);
        Assert.Empty(run.Steps);
    }

    [Fact]
    public async Task Redis_budget_is_atomic_across_instances()
    {
        using var redis = RedisFixture.Connect();
        var prefix = RedisFixture.Prefix();
        var stores = Enumerable.Range(0, 4).Select(_ => CreateRedisStore(redis, prefix)).ToArray();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            stores[i % stores.Length].TryConsumeBudgetAsync(T0, 10, TimeSpan.FromHours(1)).AsTask()));

        Assert.Equal(10, attempts.Count(granted => granted));
    }

    [Fact]
    public async Task Redis_leases_are_exclusive_renewed_and_released()
    {
        using var redis = RedisFixture.Connect();
        var prefix = RedisFixture.Prefix();
        var a = CreateRedisCoordinator(redis, prefix);
        var b = CreateRedisCoordinator(redis, prefix);

        var lease = await a.TryAcquireAsync("plan:feed-down", TimeSpan.FromMilliseconds(600));
        Assert.NotNull(lease);
        Assert.Null(await b.TryAcquireAsync("plan:feed-down", TimeSpan.FromMilliseconds(600)));

        // Held longer than its duration thanks to renewal.
        await Task.Delay(1500);
        Assert.Null(await b.TryAcquireAsync("plan:feed-down", TimeSpan.FromMilliseconds(600)));

        await lease!.DisposeAsync();
        var second = await b.TryAcquireAsync("plan:feed-down", TimeSpan.FromMilliseconds(600));
        Assert.NotNull(second);
        await second!.DisposeAsync();
    }

    [Fact]
    public async Task Redis_lease_of_a_dead_holder_expires()
    {
        using var redis = RedisFixture.Connect();
        var prefix = RedisFixture.Prefix();

        // A holder that died: the key exists, nobody renews it.
        await redis.GetDatabase().StringSetAsync($"{prefix}lease:plan:feed-down", "dead", TimeSpan.FromMilliseconds(300));
        var coordinator = CreateRedisCoordinator(redis, prefix);

        Assert.Null(await coordinator.TryAcquireAsync("plan:feed-down", TimeSpan.FromSeconds(5)));
        await Task.Delay(500);
        var lease = await coordinator.TryAcquireAsync("plan:feed-down", TimeSpan.FromSeconds(5));
        Assert.NotNull(lease);
        await lease!.DisposeAsync();
    }

    [Fact]
    public async Task Redis_cluster_plan_runs_once_across_hosts()
    {
        var connection = RedisFixture.Require();
        var prefix = RedisFixture.Prefix();
        var recorder = new EngineTests.Recorder();

        await using var a = await StartInstanceAsync("a", recorder, h => h.UseRedis(connection, o => o.KeyPrefix = prefix));
        await using var b = await StartInstanceAsync("b", recorder, h => h.UseRedis(connection, o => o.KeyPrefix = prefix));

        a.Publish("feed", SignalSeverity.Unhealthy);
        b.Publish("feed", SignalSeverity.Unhealthy);

        await WaitForAnyRunAsync(a, b);
        await Task.Delay(500);

        Assert.Equal(1, recorder.Executions);
        Assert.Single(await b.Control.GetRecentRunsAsync());
        Assert.Equal(1, (await a.Control.GetBudgetAsync()).Used);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<HomeostatTestHost> StartInstanceAsync(
        string instance,
        EngineTests.Recorder recorder,
        Action<HomeostatBuilder> configure,
        bool cluster = true)
        => HomeostatTestHost.StartAsync(
            h =>
            {
                h.Configure(o => o.InstanceId = instance);
                configure(h);
                h.AddCondition("feed-down", c => c
                    .ForSource("feed")
                    .Heal(p =>
                    {
                        p.With<EngineTests.RecordingAction>().UseVerifier<EngineTests.AlwaysHealedVerifier>();
                        if (cluster)
                        {
                            p.OncePerCluster();
                        }
                    }));
            },
            services => services.AddSingleton(recorder));

    private static async Task<HealingRunResult> WaitForAnyRunAsync(HomeostatTestHost a, HomeostatTestHost b)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var run = a.CompletedRuns.Concat(b.CompletedRuns).FirstOrDefault();
            if (run is not null)
            {
                return run;
            }

            await Task.Delay(10, cts.Token);
        }
    }

    [Fact]
    public async Task Redis_store_keeps_the_control_state_with_atomic_switch_ownership_and_answers()
    {
        using var redis = RedisFixture.Connect();
        var prefix = RedisFixture.Prefix();
        var store = CreateRedisStore(redis, prefix);
        var now = DateTimeOffset.UtcNow;

        await store.SetPauseAsync(new PauseState { At = now, By = "on-call" });
        Assert.Equal("on-call", (await store.GetControlStateAsync()).Pause?.By);
        await store.SetPauseAsync(null);
        Assert.Null((await store.GetControlStateAsync()).Pause);

        // A plan claims a free switch, takes it back from itself, but never from a person or another condition.
        Assert.True(await store.TryClaimSwitchAsync(new SwitchState { Name = "degrade", Condition = "feed-down", Instance = "a", At = now }));
        Assert.True(await store.TryClaimSwitchAsync(new SwitchState { Name = "degrade", Condition = "feed-down", Instance = "b", At = now }));
        Assert.False(await store.TryClaimSwitchAsync(new SwitchState { Name = "degrade", Condition = "disk-full", Instance = "a", At = now }));
        Assert.False(await store.TryReleaseSwitchAsync("degrade", "feed-down", "a"), "B owns it now");
        Assert.True(await store.TryReleaseSwitchAsync("degrade", "feed-down", "b"));

        await store.SetSwitchAsync("serve-cached", new SwitchState { Name = "serve-cached", By = "on-call", At = now });
        Assert.False(await store.TryClaimSwitchAsync(new SwitchState { Name = "serve-cached", Condition = "feed-down", Instance = "a", At = now }));
        Assert.False(await store.TryReleaseSwitchAsync("serve-cached", "feed-down", null));
        var state = Assert.Single((await store.GetControlStateAsync()).Switches);
        Assert.Null(state.Condition);

        // The first answer wins; an answer this version cannot read leaves the request pending.
        var request = new PendingApproval { Id = "r1", ConditionName = "feed-down", Step = 0, StepName = "renew", RequestedAt = now, ExpiresAt = now.AddMinutes(5), TriggeringSignal = new Signal("feed", SignalSeverity.Unhealthy, now) };
        await store.AddApprovalAsync(new ApprovalRecord { Request = request, Instance = "a" });
        Assert.True(await store.TryResolveApprovalAsync("r1", ApprovalStatus.Approved, "on-call"));
        Assert.False(await store.TryResolveApprovalAsync("r1", ApprovalStatus.Rejected, "someone else"));
        var answered = Assert.Single((await store.GetControlStateAsync()).Approvals);
        Assert.Equal(ApprovalStatus.Approved, answered.Status);
        Assert.Equal("on-call", answered.ResolvedBy);

        await store.AddApprovalAsync(new ApprovalRecord { Request = request with { Id = "r2" }, Instance = "a" });
        await redis.GetDatabase().HashSetAsync($"{prefix}{{control}}:answers", "r2", "Postponed\nfuture");
        Assert.Null((await store.GetControlStateAsync()).Approvals.Single(a => a.Request.Id == "r2").Status);

        await store.RemoveApprovalAsync("r1");
        Assert.Equal("r2", Assert.Single((await store.GetControlStateAsync()).Approvals).Request.Id);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("file")]
    [InlineData("redis")]
    public async Task Pause_and_switch_writes_apply_in_decision_order(string kind)
    {
        using var redis = kind == "redis" ? RedisFixture.Connect() : null;
        var path = Path.Combine(Path.GetTempPath(), $"homeostat-order-{Guid.NewGuid():N}.json");
        IHomeostatStore store = kind switch
        {
            "memory" => new InMemoryHomeostatStore(),
            "file" => new FileHomeostatStore(path),
            _ => CreateRedisStore(redis!, RedisFixture.Prefix()),
        };

        try
        {
            var t0 = DateTimeOffset.Parse("2026-11-04T10:00:00Z");

            // A resume decided after a pause, then the pause written late (it timed out on its instance): the resume stands.
            Assert.True(await store.TrySetPauseAsync(null, t0.AddSeconds(2)));
            Assert.False(await store.TrySetPauseAsync(new PauseState { At = t0.AddSeconds(1), By = "deploy" }, t0.AddSeconds(1)));
            Assert.Null((await store.GetControlStateAsync()).Pause);
            Assert.True(await store.TrySetPauseAsync(new PauseState { At = t0.AddSeconds(3) }, t0.AddSeconds(3)));
            Assert.NotNull((await store.GetControlStateAsync()).Pause);

            // A switch turned off, then the earlier turn-on written late: it stays off.
            Assert.True(await store.TrySetSwitchAsync("serve-cached", null, t0.AddSeconds(5)));
            Assert.False(await store.TrySetSwitchAsync("serve-cached", new SwitchState { Name = "serve-cached", By = "on-call", At = t0.AddSeconds(4) }, t0.AddSeconds(4)));
            Assert.Empty((await store.GetControlStateAsync()).Switches);

            // A condition's claim older than that decision is refused too; a newer one is not.
            Assert.False(await store.TryClaimSwitchAsync(new SwitchState { Name = "serve-cached", Condition = "feed-down", Instance = "a", At = t0.AddSeconds(4) }));
            var claim = new SwitchState { Name = "serve-cached", Condition = "feed-down", Instance = "a", At = t0.AddSeconds(6) };
            Assert.True(await store.TryClaimSwitchAsync(claim));

            // A release decided before that claim, written late, does not turn the newer claim off.
            Assert.False(await store.TryReleaseSwitchAsync("serve-cached", "feed-down", "a", t0.AddSeconds(5.5)));
            Assert.Single((await store.GetControlStateAsync()).Switches);

            // Released, then renewed by a renewal that raced with the release: the renewal does not turn it back on.
            Assert.True(await store.TryReleaseSwitchAsync("serve-cached", "feed-down", "a", t0.AddSeconds(7)));
            Assert.False(await store.TryClaimSwitchAsync(new SwitchState { Name = "serve-cached", Condition = "feed-down", Instance = "a", At = claim.At, RenewedAt = t0.AddSeconds(30) }));
            Assert.Empty((await store.GetControlStateAsync()).Switches);

            // On a tie, doing less wins: a turn-on or a resume stamped like the latest decision is refused; a turn-off
            // or a pause written again with its own time is accepted.
            Assert.False(await store.TrySetSwitchAsync("serve-cached", new SwitchState { Name = "serve-cached", At = t0.AddSeconds(7) }, t0.AddSeconds(7)));
            Assert.True(await store.TrySetSwitchAsync("serve-cached", null, t0.AddSeconds(7)));
            Assert.False(await store.TrySetPauseAsync(null, t0.AddSeconds(3)));
            Assert.True(await store.TrySetPauseAsync(new PauseState { At = t0.AddSeconds(3) }, t0.AddSeconds(3)));

            // A renewal is not a decision: a person's turn-off decided after the claim but before the renewal, written
            // late, still applies.
            static SwitchState Degrade(DateTimeOffset at, DateTimeOffset? renewedAt = null)
                => new() { Name = "degrade", Condition = "feed-down", Instance = "a", At = at, RenewedAt = renewedAt };
            Assert.True(await store.TryClaimSwitchAsync(Degrade(t0.AddSeconds(10))));
            Assert.True(await store.TryClaimSwitchAsync(Degrade(t0.AddSeconds(10), t0.AddSeconds(40))));
            Assert.True(await store.TrySetSwitchAsync("degrade", null, t0.AddSeconds(20)));
            Assert.DoesNotContain((await store.GetControlStateAsync()).Switches, x => x.Name == "degrade");

            // A release decided before the renewal it raced with is refused: the instance still holds the switch.
            Assert.True(await store.TryClaimSwitchAsync(Degrade(t0.AddSeconds(50))));
            Assert.True(await store.TryClaimSwitchAsync(Degrade(t0.AddSeconds(50), t0.AddSeconds(80))));
            Assert.False(await store.TryReleaseSwitchAsync("degrade", "feed-down", "a", t0.AddSeconds(70)));
            Assert.Contains((await store.GetControlStateAsync()).Switches, x => x.Name == "degrade");
        }
        finally
        {
            (store as IDisposable)?.Dispose();
            File.Delete(path);
        }
    }

    private static RedisHomeostatStore CreateRedisStore(IConnectionMultiplexer redis, string prefix)
        => new(redis, Options.Create(new RedisStoreOptions { KeyPrefix = prefix }), Options.Create(new HomeostatOptions()));

    private static RedisCoordinator CreateRedisCoordinator(IConnectionMultiplexer redis, string prefix)
        => new(redis, Options.Create(new RedisStoreOptions { KeyPrefix = prefix }), NullLogger<RedisCoordinator>.Instance);

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    internal sealed class LosableCoordinator : IHomeostatCoordinator
    {
        private readonly CancellationTokenSource _lost = new();

        public void LoseAll() => _lost.Cancel();

        public ValueTask<IHomeostatLease?> TryAcquireAsync(string resource, TimeSpan duration, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IHomeostatLease?>(new Lease(_lost.Token));

        private sealed class Lease(CancellationToken lost) : IHomeostatLease
        {
            public CancellationToken Lost => lost;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class NeverHealedVerifier : IHealingVerifier
    {
        public Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(VerificationOutcome.NotHealed);
    }
}
