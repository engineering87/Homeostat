using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using Homeostat.Detectors;
using Homeostat.Guardrails;
using Homeostat.Healing;
using Homeostat.Metrics;
using Homeostat.Persistence;
using Homeostat.Probes;
using Homeostat.Recording;
using Homeostat.Redis;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Homeostat.Tests;

/// <summary>Changes from the public API review before 1.0.0-rc.1 (the API freezes after it).</summary>
public class ApiReviewTests
{
    // ---------------------------------------------------------------- 1. UseVerifier applies to the whole plan

    [Fact]
    public async Task UseVerifier_replaces_the_verifier_of_every_step_of_the_plan()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("feed-down", c => c.ForSource("feed")
                .Heal(p => p.With("renew", _ => { }).Then("failover", _ => { }).UseVerifier<NeverHealed>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal(2, run.Steps.Count);
        Assert.All(run.Steps, s => Assert.Equal(VerificationOutcome.NotHealed, s.Verification));
    }

    private sealed class NeverHealed : IHealingVerifier
    {
        public Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(VerificationOutcome.NotHealed);
    }

    // ---------------------------------------------------------------- 2. ThreadPoolStarvationDetectorOptions

    [Fact]
    public void The_thread_pool_detector_options_are_named_after_their_method()
    {
        var parameter = typeof(HomeostatBuilder).GetMethod(nameof(HomeostatBuilder.AddThreadPoolStarvationDetector))!.GetParameters()[0];
        Assert.Equal(typeof(Action<ThreadPoolStarvationDetectorOptions>), parameter.ParameterType);
    }

    // ---------------------------------------------------------------- 3. ConditionClearedVerifier

    [Fact]
    public void The_default_verifier_has_no_public_constructor_and_still_resolves_from_the_services()
    {
        Assert.Empty(typeof(ConditionClearedVerifier).GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHomeostat();
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        // Users who decorate it resolve it as a dependency of their own verifier; transient, like before.
        var first = scope.ServiceProvider.GetRequiredService<ConditionClearedVerifier>();
        Assert.NotSame(first, scope.ServiceProvider.GetRequiredService<ConditionClearedVerifier>());
    }

    // ---------------------------------------------------------------- 4. ActionBudgetStatus

    [Fact]
    public async Task The_budget_status_is_a_nominal_record_with_required_properties()
    {
        Assert.DoesNotContain(typeof(ActionBudgetStatus).GetConstructors(), c => c.GetParameters().Length > 0);
        Assert.Null(typeof(ActionBudgetStatus).GetMethod("Deconstruct"));
        foreach (var name in new[] { nameof(ActionBudgetStatus.Used), nameof(ActionBudgetStatus.Limit), nameof(ActionBudgetStatus.Window) })
        {
            Assert.NotNull(typeof(ActionBudgetStatus).GetProperty(name)!.GetCustomAttribute<RequiredMemberAttribute>());
        }

        await using var host = await HomeostatTestHost.StartAsync(h => h.Configure(o => o.MaxActionsPerWindow = 7));
        var budget = await host.Control.GetBudgetAsync();
        Assert.Equal(7, budget.Limit);
        Assert.Equal(7, budget.Remaining);
        Assert.False(budget.IsExhausted);
        Assert.Equal(2, new ActionBudgetStatus { Used = 5, Limit = 7, Window = TimeSpan.FromMinutes(1) }.Remaining);
    }

    // ---------------------------------------------------------------- 5. Server signal thresholds named by quantity

    [Fact]
    public void Server_signal_thresholds_are_named_by_their_quantity_and_reach_the_metric_signal()
    {
        var services = new ServiceCollection();
        services.AddHomeostat(h => h
            .AddServerLatencySignal(o => { o.DegradedLatency = TimeSpan.FromMilliseconds(250); o.UnhealthyLatency = TimeSpan.FromSeconds(1); })
            .AddServerErrorRateSignal(o => { o.DegradedErrorRatio = 0.02; o.UnhealthyErrorRatio = 0.1; }));

        var watches = services.Select(d => d.ImplementationInstance).OfType<MetricWatch>().ToDictionary(w => w.Source);
        Assert.Equal((0.25, 1d), (watches["http-server:latency"].DegradedAbove, watches["http-server:latency"].UnhealthyAbove));
        Assert.Equal((0.02, 0.1), (watches["http-server:errors"].DegradedAbove, watches["http-server:errors"].UnhealthyAbove));

        Assert.Equal(TimeSpan.FromMilliseconds(500), new ServerLatencySignalOptions().DegradedLatency);
        Assert.Equal(TimeSpan.FromSeconds(2), new ServerLatencySignalOptions().UnhealthyLatency);
        Assert.Equal(0.01, new ServerErrorRateSignalOptions().DegradedErrorRatio);
        Assert.Equal(0.05, new ServerErrorRateSignalOptions().UnhealthyErrorRatio);
    }

    // ---------------------------------------------------------------- 6. AddHomeostatCheck follows the HealthChecks conventions

    [Fact]
    public void The_Homeostat_check_registration_takes_a_timeout_and_keeps_Degraded_as_its_own_failure_status()
    {
        var services = new ServiceCollection();
        services.AddHealthChecks().AddHomeostatCheck(failureStatus: HealthStatus.Unhealthy, tags: ["ready"], timeout: TimeSpan.FromSeconds(2));
        using var provider = services.BuildServiceProvider();

        var registration = Assert.Single(provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations);
        Assert.Equal("homeostat", registration.Name);
        Assert.Equal(TimeSpan.FromSeconds(2), registration.Timeout);
        Assert.Equal(HealthStatus.Degraded, registration.FailureStatus);   // a check that throws or times out: decision I6
        Assert.Contains("ready", registration.Tags);
    }

    [Theory]
    [InlineData(null, HealthStatus.Degraded)]
    [InlineData(HealthStatus.Unhealthy, HealthStatus.Unhealthy)]
    public async Task The_failure_status_is_reported_when_Homeostat_needs_attention_and_null_means_Degraded(HealthStatus? failureStatus, HealthStatus expected)
    {
        await using var host = await HomeostatTestHost.StartAsync(
            h => h.AddCondition("feed-down", c => c.ForSource("feed")),
            configureServices: services => services.AddHealthChecks().AddHomeostatCheck(failureStatus: failureStatus));

        host.Control.Pause();
        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(expected, report.Entries["homeostat"].Status);
    }

    [Fact]
    public async Task A_Homeostat_check_that_times_out_is_degraded_even_with_an_unhealthy_failure_status()
    {
        var store = new HangingBudgetStore();
        try
        {
            await using var host = await HomeostatTestHost.StartAsync(
                h => h.UseStore(_ => store),
                configureServices: services => services.AddHealthChecks()
                    .AddHomeostatCheck(failureStatus: HealthStatus.Unhealthy, timeout: TimeSpan.FromMilliseconds(100)));

            var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

            Assert.Equal(HealthStatus.Degraded, report.Entries["homeostat"].Status);
        }
        finally
        {
            store.Release();
        }
    }

    /// <summary>An in-memory store whose budget read does not answer until released.</summary>
    private sealed class HangingBudgetStore : IHomeostatStore
    {
        private readonly InMemoryHomeostatStore _inner = new();
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => _inner.GetPlanStateAsync(key, cancellationToken);

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) => _inner.UpdatePlanStateAsync(key, update, cancellationToken);

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken);

        public async ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default)
        {
            await _gate.Task.ConfigureAwait(false);
            return await _inner.GetBudgetUsedAsync(now, window, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => _inner.AppendRunAsync(run, maxRuns, cancellationToken);

        public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) => _inner.GetRecentRunsAsync(maxRuns, cancellationToken);

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) => _inner.GetControlStateAsync(cancellationToken);

        public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetPauseAsync(pause, decidedAt, cancellationToken);

        public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetSwitchAsync(name, state, decidedAt, cancellationToken);

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) => _inner.TryClaimSwitchAsync(state, cancellationToken);

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken);

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) => _inner.AddApprovalAsync(approval, cancellationToken);

        public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default) => _inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken);

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) => _inner.RemoveApprovalAsync(id, cancellationToken);
    }

    // ---------------------------------------------------------------- 7. RunId in the stored runs

    [Fact]
    public async Task A_stored_run_carries_the_run_id_of_its_result_and_events()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p.With("renew", _ => { }))));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal(run.RunId, HealingRunRecord.From(run, "a").RunId);
        IReadOnlyList<HealingRunRecord> stored = [];
        await PollyControlTests.WaitUntilAsync(() => (stored = host.Control.GetRecentRunsAsync().AsTask().GetAwaiter().GetResult()).Count > 0);
        Assert.Equal(run.RunId, Assert.Single(stored).RunId);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("file")]
    [InlineData("redis")]
    public async Task Every_store_keeps_the_run_id_of_a_run(string kind)
    {
        var record = new HealingRunRecord
        {
            RunId = "run-42",
            ConditionName = "feed-down",
            Outcome = HealingRunOutcome.Healed,
            StartedAt = DateTimeOffset.Parse("2026-11-04T10:00:00Z"),
            CompletedAt = DateTimeOffset.Parse("2026-11-04T10:01:00Z"),
            Steps = [new HealingStepRecord { Name = "renew", Succeeded = true, Verification = VerificationOutcome.Healed }],
            Instance = "a",
        };

        switch (kind)
        {
            case "memory":
            {
                var store = new InMemoryHomeostatStore();
                await store.AppendRunAsync(record, 10);
                Assert.Equal("run-42", Assert.Single(await store.GetRecentRunsAsync(10)).RunId);
                break;
            }

            case "file":
            {
                var path = Path.Combine(Path.GetTempPath(), $"homeostat-runid-{Guid.NewGuid():N}.json");
                try
                {
                    using (var store = new FileHomeostatStore(path))
                    {
                        await store.AppendRunAsync(record, 10);
                    }

                    Assert.Contains("run-42", await File.ReadAllTextAsync(path));
                    using var reopened = new FileHomeostatStore(path);   // read back from the file, not from memory
                    Assert.Equal("run-42", Assert.Single(await reopened.GetRecentRunsAsync(10)).RunId);
                }
                finally
                {
                    File.Delete(path);
                    File.Delete(path + ".lock");
                }

                break;
            }

            default:
            {
                using var redis = RedisFixture.Connect();
                var store = new RedisHomeostatStore(redis, Options.Create(new RedisStoreOptions { KeyPrefix = RedisFixture.Prefix() }), Options.Create(new HomeostatOptions()));
                await store.AppendRunAsync(record, 10);
                Assert.Equal("run-42", Assert.Single(await store.GetRecentRunsAsync(10)).RunId);
                break;
            }
        }
    }

    [Fact]
    public void A_run_stored_before_the_run_id_was_recorded_reads_with_a_null_run_id()
    {
        const string old = """{"conditionName":"feed-down","outcome":"Healed","startedAt":"2026-11-04T10:00:00+00:00","completedAt":"2026-11-04T10:01:00+00:00","steps":[],"instance":"a"}""";
        var run = System.Text.Json.JsonSerializer.Deserialize(old, RedisJsonContext.Default.HealingRunRecord)!;
        Assert.Null(run.RunId);
        Assert.Equal("feed-down", run.ConditionName);
    }

    // ---------------------------------------------------------------- 8. and 9. HomeostatTestHost

    [Fact]
    public async Task The_test_host_publishes_a_value_and_takes_its_services_as_configureServices()
    {
        await using var host = await HomeostatTestHost.StartAsync(
            h => h.AddCondition("queue-long", c => c.ForSource("queue").WhenValueAbove(100)),
            configureServices: services => services.AddSingleton(new Marker()));

        Assert.NotNull(host.Services.GetService<Marker>());
        Assert.True(host.Publish("queue", SignalSeverity.Healthy, value: 150));
        await host.WaitForConditionAsync("queue-long", Conditions.ConditionStatus.Active);
        Assert.Equal(150, host.Control.GetConditions().Single().LastSignal!.Value);
    }

    private sealed class Marker;

    // ---------------------------------------------------------------- 10. Members for integration packages

    [Fact]
    public void Members_meant_for_integration_packages_are_hidden_from_IntelliSense()
    {
        static EditorBrowsableState? State(MemberInfo member) => member.GetCustomAttribute<EditorBrowsableAttribute>()?.State;

        Assert.Equal(EditorBrowsableState.Never, State(typeof(ProbeOptions).GetMethod(nameof(ProbeOptions.CopyTo))!));
        var overloads = typeof(HomeostatCallSignalExtensions).GetMethods().Where(m => m.Name == nameof(HomeostatCallSignalExtensions.AddCallSignals)).ToArray();
        Assert.Equal(EditorBrowsableState.Never, State(overloads.Single(m => m.GetParameters()[0].ParameterType == typeof(IServiceCollection))));
        Assert.Null(State(overloads.Single(m => m.GetParameters()[0].ParameterType == typeof(HomeostatBuilder))));
    }

    // ---------------------------------------------------------------- 11. A route passed as the policy

    [Fact]
    public void A_route_passed_as_the_policy_is_refused_at_map_time()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddHomeostat();
        var app = builder.Build();

        foreach (var map in new Action[]
        {
            () => app.MapHomeostatControl("/ops"),
            () => app.MapHomeostatStatus("/ops"),
            () => app.MapHomeostatDashboard("/ops"),
            () => app.MapHomeostatAlerts("/ops"),
        })
        {
            var error = Assert.Throws<ArgumentException>(map);
            Assert.Contains("looks like a route prefix", error.Message);
            Assert.Contains("prefix: \"/ops\"", error.Message);
        }

        // The policy by position and the prefix by name still map.
        app.MapHomeostatControl("operations", prefix: "/ops");
        app.MapHomeostatAlerts("monitoring", prefix: "/ops/alerts");
    }

    // ---------------------------------------------------------------- 14. ReplayReport

    [Fact]
    public async Task A_replay_report_counts_its_signals_over_a_since_until_range()
    {
        var start = DateTimeOffset.Parse("2026-11-04T10:00:00Z");
        var report = await HomeostatReplay.RunAsync(
            [
                new RecordedSignal { Timestamp = start, Source = "feed", Severity = SignalSeverity.Unhealthy },
                new RecordedSignal { Timestamp = start.AddMinutes(1), Source = "feed", Severity = SignalSeverity.Healthy },
            ],
            h => h.AddCondition("feed-down", c => c.ForSource("feed")));

        Assert.Equal(2, report.SignalCount);
        Assert.Equal(start, report.Since);
        Assert.Equal(start.AddMinutes(1), report.Until);
        Assert.StartsWith("Replay of 2 signals from", report.ToText());

        // The CLI's JSON keeps its names.
        var json = Homeostat.Cli.Replay.ToJson(report);
        Assert.Contains("\"signals\": 2", json);
        Assert.Contains("\"from\":", json);
        Assert.Contains("\"to\":", json);
    }
}
