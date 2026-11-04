using Homeostat.Conditions;
using Homeostat.Healing;
using Homeostat.Persistence;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Homeostat.Tests;

/// <summary>
/// The pause, the application switches and the approval requests are kept in the store, so every instance that shares
/// it sees them. Two test hosts sharing one in-memory store stand for two instances.
/// </summary>
public class SharedControlTests
{
    private static Task<HomeostatTestHost> StartInstanceAsync(string instance, IHomeostatStore store, Action<HomeostatBuilder>? configure = null)
        => HomeostatTestHost.StartAsync(h =>
        {
            h.UseStore(_ => store).Configure(o =>
            {
                o.InstanceId = instance;
                o.ControlRefreshInterval = TimeSpan.FromSeconds(5);
            });
            configure?.Invoke(h);
        });

    /// <summary>Lets the instance read the store (its refresh timer follows the host's simulated clock).</summary>
    private static async Task RefreshAsync(HomeostatTestHost host)
    {
        host.Time.Advance(TimeSpan.FromSeconds(5));
        await host.Services.GetRequiredService<Engine.SharedControl>().TryRefreshAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_pause_set_on_one_instance_stops_the_others()
    {
        var store = new InMemoryHomeostatStore();
        await using var a = await StartInstanceAsync("a", store);
        await using var b = await StartInstanceAsync("b", store);

        await a.Control.PauseAsync(by: "on-call");
        Assert.True(a.Control.IsPaused);
        Assert.Equal("paused by on-call", a.Control.PauseReason);

        await RefreshAsync(b);
        Assert.True(b.Control.IsPaused);

        await b.Control.ResumeAsync();
        await RefreshAsync(a);
        Assert.False(a.Control.IsPaused);
    }

    [Fact]
    public async Task A_timed_pause_ends_by_itself_on_every_instance()
    {
        var store = new InMemoryHomeostatStore();
        await using var a = await StartInstanceAsync("a", store);

        await a.Control.PauseAsync(a.Time.GetUtcNow().AddMinutes(10));
        Assert.StartsWith("paused until", a.Control.PauseReason);

        a.Time.Advance(TimeSpan.FromMinutes(11));
        Assert.False(a.Control.IsPaused);
    }

    [Fact]
    public async Task An_approval_requested_on_one_instance_can_be_answered_on_another()
    {
        var store = new InMemoryHomeostatStore();
        var executed = 0;
        await using var a = await StartInstanceAsync("a", store, h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p.With("renew", _ => Interlocked.Increment(ref executed)).RequireApproval().VerifyWith<EngineTests.AlwaysHealedVerifier>())));
        await using var b = await StartInstanceAsync("b", store);

        a.Publish("feed", SignalSeverity.Unhealthy);
        var request = await a.WaitForApprovalAsync("feed-down");

        // B lists the request and answers it; A applies the answer at its next refresh.
        Assert.Equal(request.Id, Assert.Single(await b.Approvals.GetPendingAsync()).Id);
        Assert.True(await b.Approvals.ApproveAsync(request.Id, "on-call"));
        Assert.False(await b.Approvals.ApproveAsync(request.Id), "an answered request cannot be answered again");

        await RefreshAsync(a);
        var run = await a.WaitForRunAsync("feed-down");
        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Equal(1, executed);
        Assert.Empty((await store.GetControlStateAsync()).Approvals);
    }

    [Fact]
    public async Task Application_switches_reach_every_instance_and_instance_switches_stay_local()
    {
        var store = new InMemoryHomeostatStore();
        await using var a = await StartInstanceAsync("a", store);
        await using var b = await StartInstanceAsync("b", store);
        var changes = new List<(string, bool)>();
        b.Switches.Changed += (_, e) => changes.Add((e.Name, e.IsOn));

        await a.Switches.TurnOnAsync("serve-cached-forecast", by: "on-call");
        await a.Switches.TurnOnAsync("verbose-tracing", SwitchScope.Instance);
        await RefreshAsync(b);

        Assert.True(b.Switches.IsOn("serve-cached-forecast"));
        Assert.False(b.Switches.IsOn("verbose-tracing"));
        Assert.True(a.Switches.IsOn("verbose-tracing"));
        Assert.Equal([("serve-cached-forecast", true)], changes);
        Assert.Equal("on-call", Assert.Single((await store.GetControlStateAsync()).Switches).By);

        await b.Switches.TurnOffAsync("serve-cached-forecast");
        await RefreshAsync(a);
        Assert.False(a.Switches.IsOn("serve-cached-forecast"));
    }

    [Fact]
    public async Task A_cluster_plan_turns_on_an_application_switch_that_its_instance_turns_off_when_the_condition_clears()
    {
        var store = new InMemoryHomeostatStore();
        void Configure(HomeostatBuilder h) => h.UseCoordinator(_ => Coordinator).AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p.TurnOnSwitch("serve-cached-forecast").OncePerCluster().VerifyWith<EngineTests.AlwaysHealedVerifier>()));
        await using var a = await StartInstanceAsync("a", store, Configure);
        await using var b = await StartInstanceAsync("b", store, Configure);

        a.Publish("feed", SignalSeverity.Unhealthy);
        await a.WaitForRunAsync("feed-down");
        var state = Assert.Single((await store.GetControlStateAsync()).Switches);
        Assert.Equal("feed-down", state.Condition);
        await RefreshAsync(b);
        Assert.True(b.Switches.IsOn("serve-cached-forecast"));

        // The condition was active on B too; it clears there first, while A, which ran the plan, still needs the switch.
        b.Publish("feed", SignalSeverity.Unhealthy);
        await b.WaitForConditionAsync("feed-down", ConditionStatus.Active);
        b.Publish("feed", SignalSeverity.Healthy);
        await b.WaitForConditionAsync("feed-down", ConditionStatus.Inactive);
        await Task.Delay(100);
        Assert.Equal("a", Assert.Single((await store.GetControlStateAsync()).Switches).Instance);

        a.Publish("feed", SignalSeverity.Healthy);
        await a.WaitForConditionAsync("feed-down", ConditionStatus.Inactive);
        await PollyControlTests.WaitUntilAsync(() => store.GetControlStateAsync().AsTask().Result.Switches.Count == 0);
    }

    [Fact]
    public async Task A_switch_turned_on_elsewhere_is_turned_off_before_this_instance_sees_it()
    {
        var store = new InMemoryHomeostatStore();
        await using var a = await StartInstanceAsync("a", store, h => { });
        await using var b = await StartInstanceAsync("b", store, h => { });

        await a.Switches.TurnOnAsync("serve-cached-forecast");
        Assert.False(b.Switches.IsOn("serve-cached-forecast"), "B has not refreshed yet");
        await b.Switches.TurnOffAsync("serve-cached-forecast");

        Assert.Empty((await store.GetControlStateAsync()).Switches);
    }

    [Fact]
    public async Task An_instance_plan_does_not_keep_a_copy_of_a_switch_a_person_turned_on_for_the_application()
    {
        var store = new InMemoryHomeostatStore();
        await using var host = await StartInstanceAsync("a", store, h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p.TurnOnSwitch("serve-cached-forecast").VerifyWith<EngineTests.AlwaysHealedVerifier>())));

        await host.Switches.TurnOnAsync("serve-cached-forecast", by: "on-call");
        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("feed-down");
        await host.Switches.TurnOffAsync("serve-cached-forecast");

        Assert.False(host.Switches.IsOn("serve-cached-forecast"));
    }

    [Fact]
    public async Task A_switch_handler_may_change_switches_while_the_change_is_raised()
    {
        var store = new InMemoryHomeostatStore();
        await using var host = await StartInstanceAsync("a", store);
        var seen = new List<string>();
        host.Switches.Changed += (_, e) =>
        {
            seen.Add($"{e.Name}:{e.IsOn}");
            if (e.Name == "degrade" && e.IsOn)
            {
                // Blocking on purpose, as some handlers do: it must not wait for the change being raised.
                host.Switches.TurnOnAsync("serve-cached-forecast").AsTask().GetAwaiter().GetResult();
            }
        };

        await host.Switches.TurnOnAsync("degrade").AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["degrade:True", "serve-cached-forecast:True"], seen);
    }

    [Fact]
    public async Task Homeostat_counts_as_paused_until_the_control_state_is_read()
    {
        var store = new InMemoryHomeostatStore();
        await store.SetPauseAsync(new PauseState { At = DateTimeOffset.UtcNow, By = "on-call" });
        var services = new ServiceCollection().AddLogging();
        services.AddHomeostat(h => h.UseStore(_ => store));
        await using var provider = services.BuildServiceProvider();
        var control = provider.GetRequiredService<IHomeostatControl>();

        Assert.True(control.IsPaused);
        Assert.StartsWith("waiting for the control state", control.PauseReason);

        await provider.GetRequiredService<Engine.SharedControl>().TryRefreshAsync(CancellationToken.None);
        Assert.Equal("paused by on-call", control.PauseReason);
    }

    private static readonly Coordination.InMemoryCoordinator Coordinator = new();

    [Fact]
    public async Task A_plan_never_takes_over_an_application_switch_turned_on_by_a_person()
    {
        var store = new InMemoryHomeostatStore();
        await using var host = await StartInstanceAsync("a", store, h => h.UseCoordinator(_ => new Coordination.InMemoryCoordinator()).AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p.TurnOnSwitch("serve-cached-forecast").OncePerCluster().VerifyWith<EngineTests.AlwaysHealedVerifier>())));

        await host.Switches.TurnOnAsync("serve-cached-forecast", by: "on-call");
        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("feed-down");
        host.Publish("feed", SignalSeverity.Healthy);
        await host.WaitForConditionAsync("feed-down", ConditionStatus.Inactive);
        await Task.Delay(50);

        Assert.True(host.Switches.IsOn("serve-cached-forecast"));
        Assert.Null(Assert.Single((await store.GetControlStateAsync()).Switches).Condition);
    }

    [Fact]
    public async Task A_held_plan_is_released_without_waiting_for_the_condition_to_clear()
    {
        var executed = 0;
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p.With("renew", _ => Interlocked.Increment(ref executed)).RequireApproval().VerifyWith<EngineTests.AlwaysHealedVerifier>().Cooldown(TimeSpan.Zero))));

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.Approvals.RejectAsync((await host.WaitForApprovalAsync("feed-down")).Id, "on-call");
        Assert.Equal(HealingRunOutcome.NotApproved, (await host.WaitForRunAsync("feed-down")).Outcome);

        // Held: a new triggering signal starts nothing.
        host.Publish("feed", SignalSeverity.Unhealthy);
        await Task.Delay(100);
        Assert.Single(host.CompletedRuns);

        Assert.True(await host.Control.ReleaseHoldAsync("feed-down"));
        Assert.False(await host.Control.ReleaseHoldAsync("no-such-condition"));
        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.Approvals.ApproveAsync((await host.WaitForApprovalAsync("feed-down")).Id, "on-call");
        await PollyControlTests.WaitUntilAsync(() => host.CompletedRuns.Count == 2);
        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task The_file_store_keeps_the_pause_and_the_switches_turned_on_by_a_person()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homeostat-{Guid.NewGuid():N}.json");
        try
        {
            using (var first = new FileHomeostatStore(path))
            {
                await first.SetPauseAsync(new PauseState { By = "on-call", At = DateTimeOffset.UtcNow });
                await first.SetSwitchAsync("serve-cached-forecast", new SwitchState { Name = "serve-cached-forecast", At = DateTimeOffset.UtcNow });
                await first.SetSwitchAsync("degrade", new SwitchState { Name = "degrade", Condition = "feed-down", At = DateTimeOffset.UtcNow });
            }

            using var reopened = new FileHomeostatStore(path);
            var state = await reopened.GetControlStateAsync();
            Assert.Equal("on-call", state.Pause?.By);
            Assert.Equal("serve-cached-forecast", Assert.Single(state.Switches).Name);   // the plan's switch is not kept
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Options_cannot_change_once_Homeostat_has_started()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddCondition("feed-down", c => c.ForSource("feed")));
        var options = host.Services.GetRequiredService<IOptions<HomeostatOptions>>().Value;

        Assert.Throws<InvalidOperationException>(() => options.DryRun = true);
        Assert.Throws<InvalidOperationException>(() => options.MaxActionsPerWindow = 1_000);
    }

    [Fact]
    public async Task A_compensating_action_does_not_run_while_Homeostat_is_paused()
    {
        var undone = 0;
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddCondition("feed-down", c => c
            .ForSource("feed")
            .Heal(p => p
                .With("switch-provider", _ => { })
                .UndoWith("switch-back", (_, _) => { Interlocked.Increment(ref undone); return Task.CompletedTask; })
                .VerifyWith<PausingVerifier>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal(0, undone);
        Assert.Null(run.Steps[0].UndoSucceeded);   // skipped, not failed
    }

    /// <summary>An operator pauses Homeostat while the step is being verified, and the step did not help.</summary>
    internal sealed class PausingVerifier(IHomeostatControl control) : IHealingVerifier
    {
        public async Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
        {
            await control.PauseAsync(by: "on-call", cancellationToken: cancellationToken);
            return VerificationOutcome.NotHealed;
        }
    }

    [Fact]
    public void A_pending_cause_without_signals_stops_suppressing_its_symptoms()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var cause = new ConditionBuilder("threadpool-starved", new ServiceCollection()).ForSource("threadpool").SustainedFor(TimeSpan.FromMinutes(2)).Build();
        var symptom = new ConditionBuilder("feed-down", new ServiceCollection()).ForSource("feed").SuppressedBy("threadpool-starved").Build();
        var set = new ConditionSet([cause, symptom], time);

        set.Get("threadpool-starved")!.Observe(new Signal("threadpool", SignalSeverity.Unhealthy, time.GetUtcNow()));
        Assert.Equal("threadpool-starved", set.FindActiveSuppressor(symptom));

        // The detector stopped reporting: after its SustainedFor without any signal, the pending cause is stale.
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(set.FindActiveSuppressor(symptom));
    }

    [Fact]
    public void A_removed_condition_is_retired_and_ignores_signals()
    {
        var tracker = new ConditionTracker(new ConditionBuilder("feed-down", new ServiceCollection()).ForSource("feed").Build());
        tracker.Observe(new Signal("feed", SignalSeverity.Unhealthy, DateTimeOffset.UtcNow));
        Assert.Equal(ConditionStatus.Active, tracker.Snapshot().Status);

        tracker.Retire();
        Assert.Equal(ConditionStatus.Inactive, tracker.Snapshot().Status);
        tracker.Observe(new Signal("feed", SignalSeverity.Unhealthy, DateTimeOffset.UtcNow));
        Assert.Equal(ConditionStatus.Inactive, tracker.Snapshot().Status);
    }

    [Fact]
    public void Action_and_verification_timeouts_belong_to_the_previous_step()
    {
        var builder = new HealingPlanBuilder(new ServiceCollection());
        Assert.Throws<InvalidOperationException>(() => builder.ActionTimeout(TimeSpan.FromMinutes(1)));

        builder.With("renew", _ => { }).ActionTimeout(TimeSpan.FromMinutes(2))
            .Then("rollout", _ => { }).VerificationTimeout(TimeSpan.FromMinutes(10));
        var plan = builder.Build("feed-down");

        Assert.Equal(TimeSpan.FromMinutes(2), plan.Steps[0].ActionTimeout);
        Assert.Null(plan.Steps[0].VerificationTimeout);
        Assert.Null(plan.Steps[1].ActionTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), plan.Steps[1].VerificationTimeout);
    }
}

/// <summary>Small API rules settled before 1.0.</summary>
public class ApiRuleTests
{
    [Fact]
    public void Alerts_need_a_source_prefix()
        => Assert.Throws<ArgumentException>(() => new Homeostat.AspNetCore.AlertIngestionOptions { SourcePrefix = "" }.Validate());

    [Fact]
    public void The_default_http_failure_rule_is_public_and_combinable()
    {
        using var serverError = new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway);
        using var conflict = new HttpResponseMessage(System.Net.HttpStatusCode.Conflict);
        Assert.True(Homeostat.Http.HttpClientSignalOptions.IsServiceFailure(serverError));
        Assert.False(Homeostat.Http.HttpClientSignalOptions.IsServiceFailure(conflict));
        Assert.Equal<Func<HttpResponseMessage, bool>>(Homeostat.Http.HttpClientSignalOptions.IsServiceFailure, new Homeostat.Http.HttpClientSignalOptions().IsFailure);
    }

    [Fact]
    public async Task A_sink_without_an_event_filter_receives_every_kind()
    {
        var received = new System.Collections.Concurrent.ConcurrentBag<Homeostat.Notifications.HomeostatEventKind>();
        await using (var host = await HomeostatTestHost.StartAsync(
            h => h.AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p.With("renew", _ => { }).VerifyWith<EngineTests.AlwaysHealedVerifier>()))
                .AddEventSink<EverythingSink>(),
            services => services.AddSingleton(received)))
        {
            host.Publish("feed", SignalSeverity.Unhealthy);
            await host.WaitForRunAsync("feed-down");
        }

        Assert.Contains(Homeostat.Notifications.HomeostatEventKind.ConditionActivated, received);
        Assert.Contains(Homeostat.Notifications.HomeostatEventKind.StepCompleted, received);
        Assert.Contains(Homeostat.Notifications.HomeostatEventKind.RunCompleted, received);
    }

    internal sealed class EverythingSink(System.Collections.Concurrent.ConcurrentBag<Homeostat.Notifications.HomeostatEventKind> received) : Homeostat.Notifications.IHomeostatEventSink
    {
        public Task WriteAsync(Homeostat.Notifications.HomeostatEvent homeostatEvent, CancellationToken cancellationToken)
        {
            Assert.False(string.IsNullOrEmpty(homeostatEvent.Application));
            received.Add(homeostatEvent.Kind);
            return Task.CompletedTask;
        }
    }
}
