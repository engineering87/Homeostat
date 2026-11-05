using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Homeostat.Conditions;
using Homeostat.Detectors;
using Homeostat.Diagnostics;
using Homeostat.Healing;
using Homeostat.Healing.Actions;
using Homeostat.Notifications;
using Homeostat.Persistence;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Homeostat.Tests;

/// <summary>Fixes of a review of the core: memory bounds, store calls that never answer, compensations, detectors.</summary>
public class CoreReviewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- E1: occurrences

    [Fact]
    public void Occurrences_keep_only_as_many_matches_as_the_count()
    {
        var builder = new ConditionBuilder("errors", new ServiceCollection());
        builder.ForSource("feed").Occurrences(3, TimeSpan.FromHours(1));
        var tracker = new ConditionTracker(builder.Build());

        for (var i = 0; i < 10_000; i++)
        {
            tracker.Observe(new Signal("feed", SignalSeverity.Unhealthy, T0.AddMilliseconds(i)));
        }

        Assert.Equal(ConditionStatus.Active, tracker.Snapshot().Status);
        var matches = MatchesOf(tracker.Definition);
        Assert.InRange(matches.Count, 0, 3);

        // Same results as before: still active within the window, over once the matches leave it.
        Assert.Equal(ConditionTransition.None, tracker.Observe(new Signal("feed", SignalSeverity.Healthy, T0.AddMinutes(30))));
        Assert.Equal(ConditionStatus.Active, tracker.Snapshot().Status);
        Assert.Equal(ConditionTransition.Cleared, tracker.Observe(new Signal("feed", SignalSeverity.Healthy, T0.AddHours(2))));
    }

    private static Queue<DateTimeOffset> MatchesOf(ConditionDefinition definition)
    {
        var target = definition.Record!.Target!;
        var field = target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(f => f.FieldType == typeof(Queue<DateTimeOffset>));
        return (Queue<DateTimeOffset>)field.GetValue(target)!;
    }

    // ---------------------------------------------------------------- E2, E9: seasonal baselines

    [Fact]
    public async Task A_periodic_baseline_save_that_never_answers_does_not_hold_the_shutdown_and_is_not_started_again()
    {
        var store = new ScriptedStore { Documents = true, SetDocument = (_, _) => new TaskCompletionSource().Task };
        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .UseStore(_ => store)
                .AddCondition("latency-seasonal", c => c.ForSource("latency").WhenSeasonallyHigh(4, Seasonality.Daily())),
            options: new HomeostatTestHostOptions { AutoAdvanceStep = null });

        for (var i = 0; i < 10; i++)
        {
            host.Signals.TryPublish("latency", SignalSeverity.Healthy, null, 10 + i);
        }

        // The save of the periodic loop (every 5 simulated minutes) starts and hangs.
        var waited = Stopwatch.StartNew();
        while (store.SetDocumentCalls == 0 && waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            host.Time.Advance(SeasonalBaselineKeeper.SaveInterval);
            await Task.Delay(50);
        }

        Assert.Equal(1, store.SetDocumentCalls);

        var keeper = host.Services.GetRequiredService<SeasonalBaselineKeeper>();
        var watch = Stopwatch.StartNew();
        await keeper.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"stopping took {watch.Elapsed}");

        // The final save does not start a second call while the first still runs.
        Assert.Equal(1, store.SetDocumentCalls);
    }

    [Fact]
    public async Task Startup_does_not_wait_for_the_saved_baselines()
    {
        var store = new ScriptedStore { Documents = true, GetDocument = (_, _) => new TaskCompletionSource<string?>().Task };
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat(h => h
            .UseStore(_ => store)
            .AddCondition("latency-seasonal", c => c.ForSource("latency").WhenSeasonallyHigh(4, Seasonality.Daily())));
        using var host = builder.Build();

        var watch = Stopwatch.StartNew();
        await host.StartAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"startup took {watch.Elapsed}");

        // The engine waits for the baselines instead, at most StoreWait.
        var keeper = host.Services.GetRequiredService<SeasonalBaselineKeeper>();
        await keeper.Ready.WaitAsync(SeasonalBaselineKeeper.StoreWait + TimeSpan.FromSeconds(10));

        watch.Restart();
        await host.StopAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"shutdown took {watch.Elapsed}");
    }

    // ---------------------------------------------------------------- E3: abandoned store calls

    [Fact]
    public async Task A_plan_state_read_that_never_answers_is_not_started_again_by_each_signal()
    {
        var reads = 0;
        var store = new ScriptedStore
        {
            GetPlanState = (_, _) =>
            {
                Interlocked.Increment(ref reads);
                return new TaskCompletionSource<PlanState>().Task;   // never returns, ignores its token
            },
        };
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .UseStore(_ => store)
            .Configure(o => o.DefaultCooldown = TimeSpan.FromSeconds(1))
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p.With("renew", _ => { }))));

        host.Publish("feed", SignalSeverity.Unhealthy);
        await PollyControlTests.WaitUntilAsync(() => Volatile.Read(ref reads) == 1);

        // Past the read's timeout (10 s, real time): the run gave up, failing closed.
        await Task.Delay(Engine.SharedControl.WriteTimeout + TimeSpan.FromSeconds(1));
        for (var i = 0; i < 5; i++)
        {
            host.Publish("feed", SignalSeverity.Unhealthy);
            await Task.Delay(50);
        }

        await Task.Delay(300);
        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.Empty(host.CompletedRuns);
    }

    [Fact]
    public async Task A_history_read_that_never_answers_is_not_started_again_by_each_request()
    {
        var reads = 0;
        var store = new ScriptedStore
        {
            History = true,
            GetConditionEvents = (_, _) =>
            {
                Interlocked.Increment(ref reads);
                return new TaskCompletionSource<IReadOnlyList<ConditionEventRecord>>().Task;
            },
        };
        var history = new ConditionHistory(store, Options.Create(new HomeostatOptions()), NullLogger<ConditionHistory>.Instance, new FakeTimeProvider(T0));
        history.Record("feed-down", ConditionEventKind.Activated, new Signal("feed", SignalSeverity.Unhealthy, T0), T0);

        using (var first = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await history.ReadAsync(first.Token));
        }

        // The first read still runs: the next request answers at once with this instance's events, without a new read.
        var watch = Stopwatch.StartNew();
        var events = await history.ReadAsync(CancellationToken.None);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"the read took {watch.Elapsed}");
        Assert.Single(events);
        Assert.Equal(1, Volatile.Read(ref reads));
    }

    [Fact]
    public async Task A_history_write_that_never_answers_is_waited_for_again_instead_of_started_again()
    {
        var writes = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ScriptedStore
        {
            History = true,
            AppendConditionEvent = (_, _) =>
            {
                Interlocked.Increment(ref writes);
                return release.Task;   // ignores its token
            },
        };
        var time = new FakeTimeProvider(T0);
        var history = new ConditionHistory(store, Options.Create(new HomeostatOptions()), NullLogger<ConditionHistory>.Instance, time);
        await history.StartAsync(CancellationToken.None);
        history.Record("feed-down", ConditionEventKind.Activated, new Signal("feed", SignalSeverity.Unhealthy, T0), T0);
        await PollyControlTests.WaitUntilAsync(() => Volatile.Read(ref writes) == 1);

        // Past the write's timeout (real time), then past the retry interval (simulated): no second write.
        await Task.Delay(Engine.SharedControl.WriteTimeout + TimeSpan.FromSeconds(1));
        time.Advance(ConditionHistory.RetryInterval + TimeSpan.FromSeconds(1));
        await Task.Delay(300);
        Assert.Equal(1, Volatile.Read(ref writes));

        // Once it ends, the event counts as written: not written a second time.
        release.SetResult();
        time.Advance(ConditionHistory.RetryInterval + TimeSpan.FromSeconds(1));
        await history.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, Volatile.Read(ref writes));
    }

    // ---------------------------------------------------------------- E4: compensation after an abandoned action

    [Fact]
    public async Task The_compensation_of_an_abandoned_action_runs_once_the_action_has_ended()
    {
        var order = new ConcurrentQueue<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await HomeostatTestHost.StartAsync(
            h => h.AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                .With("switch-to-backup", async (_, _) =>
                {
                    started.TrySetResult();
                    await release.Task;   // ignores its token
                    order.Enqueue("action ended");
                })
                .ActionTimeout(TimeSpan.FromMinutes(1))
                .UndoWith("switch-to-primary", (_, _) =>
                {
                    order.Enqueue("undo");
                    return Task.CompletedTask;
                }))),
            options: new HomeostatTestHostOptions { AutoAdvanceStep = null });

        host.Publish("feed", SignalSeverity.Unhealthy);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.Time.Advance(TimeSpan.FromMinutes(1));   // the action times out, and is abandoned a moment later

        await Task.Delay(1000);
        Assert.Empty(order);   // not compensated while the action may still apply its change

        release.SetResult();
        var run = await host.WaitForRunAsync("feed-down");
        Assert.Equal(["action ended", "undo"], order.ToArray());
        var step = Assert.Single(run.Steps);
        Assert.IsType<TimeoutException>(step.Error);
        Assert.True(step.UndoSucceeded);
    }

    [Fact]
    public async Task The_compensation_of_an_action_that_never_ends_is_not_run_and_the_run_ends()
    {
        var undos = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await HomeostatTestHost.StartAsync(
            h => h.AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                .With("switch-to-backup", (_, _) =>
                {
                    started.TrySetResult();
                    return new TaskCompletionSource().Task;   // never ends, ignores its token
                })
                .ActionTimeout(TimeSpan.FromMinutes(1))
                .UndoWith("switch-to-primary", (_, _) =>
                {
                    Interlocked.Increment(ref undos);
                    return Task.CompletedTask;
                }))),
            options: new HomeostatTestHostOptions { AutoAdvanceStep = null });

        host.Publish("feed", SignalSeverity.Unhealthy);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The action's timeout, then the wait for it to end (bounded by the compensation's timeout).
        var running = host.WaitForRunAsync("feed-down", TimeSpan.FromSeconds(20));
        while (!running.IsCompleted)
        {
            host.Time.Advance(TimeSpan.FromMinutes(1));
            await Task.WhenAny(running, Task.Delay(200));
        }

        var step = Assert.Single((await running).Steps);
        Assert.IsType<TimeoutException>(step.Error);
        Assert.False(step.UndoSucceeded);
        Assert.Equal(0, Volatile.Read(ref undos));
    }

    // ---------------------------------------------------------------- E6: restart counters

    [Fact]
    public async Task Restart_counters_carry_an_end_so_the_store_retention_removes_them()
    {
        var path = Path.Combine(Path.GetTempPath(), $"homeostat-restarts-{Guid.NewGuid():N}.json");
        try
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2020-03-01T10:00:00Z"));
            var limits = new RestartLimits(TimeSpan.Zero, MaxRestarts: 3, TimeSpan.FromHours(1));
            var window = time.GetUtcNow().UtcTicks / limits.Window.Ticks;
            string attemptsKey, allowedKey;
            using (var store = new FileHomeostatStore(path))
            {
                var collection = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(time);
                collection.AddHomeostat(h => h.UseStore(_ => store));
                await using var services = collection.BuildServiceProvider();
                var application = services.GetRequiredService<IOptions<HomeostatOptions>>().Value.ApplicationName;
                attemptsKey = $"restarts/{application}/attempts/{window}";
                allowedKey = $"restarts/{application}/allowed/{window}";
                var context = new HealingContext("pool-exhausted", new Signal("pool", SignalSeverity.Unhealthy, time.GetUtcNow()), 0, "restart",
                    time.GetUtcNow(), services, () => throw new NotSupportedException());

                await RestartGuard.EnsureAllowedAsync(context, limits, CancellationToken.None);

                // The end of the time each counter is read: attempts to the end of their window, allowed to the next one's.
                Assert.Equal(new DateTimeOffset((window + 1) * limits.Window.Ticks, TimeSpan.Zero), (await store.GetPlanStateAsync(attemptsKey)).LastRunEndedAt);
                Assert.Equal(new DateTimeOffset((window + 2) * limits.Window.Ticks, TimeSpan.Zero), (await store.GetPlanStateAsync(allowedKey)).LastRunEndedAt);
                Assert.Equal(1, (await store.GetPlanStateAsync(allowedKey)).ConsecutiveFailures);
            }

            // Years later (real time), the file store's retention drops them.
            using var reopened = new FileHomeostatStore(path);
            Assert.Equal(0, (await reopened.GetPlanStateAsync(attemptsKey)).ConsecutiveFailures);
            Assert.Equal(0, (await reopened.GetPlanStateAsync(allowedKey)).ConsecutiveFailures);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- E7: clock skew

    [Fact]
    public async Task A_signal_from_a_clock_ahead_is_processed_as_of_now_and_past_ones_are_kept()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat(h => h.AddCondition("feed-down", c => c.ForSource("feed")));
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var publisher = host.Services.GetRequiredService<ISignalPublisher>();
            var tracker = host.Services.GetRequiredService<ConditionSet>().Trackers.Single();

            var before = DateTimeOffset.UtcNow;
            publisher.TryPublish(new Signal("feed", SignalSeverity.Unhealthy, before.AddDays(1)));
            await PollyControlTests.WaitUntilAsync(() => tracker.Snapshot().LastSignalAt is not null);
            Assert.InRange(tracker.Snapshot().LastSignalAt!.Value, before, DateTimeOffset.UtcNow);
            Assert.InRange(tracker.Snapshot().ActiveSince!.Value, before, DateTimeOffset.UtcNow);

            // Within the tolerance, or in the past: unchanged.
            var soon = DateTimeOffset.UtcNow.AddSeconds(20);
            publisher.TryPublish(new Signal("feed", SignalSeverity.Unhealthy, soon));
            await PollyControlTests.WaitUntilAsync(() => tracker.Snapshot().LastSignalAt == soon);
            var past = DateTimeOffset.UtcNow.AddHours(-1);
            publisher.TryPublish(new Signal("feed", SignalSeverity.Unhealthy, past));
            await PollyControlTests.WaitUntilAsync(() => tracker.Snapshot().LastSignalAt == past);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    // ---------------------------------------------------------------- E8, E13: verifier and step scope

    [Fact]
    public async Task A_verifier_whose_constructor_throws_makes_the_step_inconclusive_and_keeps_its_result()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p.With("renew", _ => { }).VerifyWith<UnbuildableVerifier>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal(HealingRunOutcome.Inconclusive, run.Outcome);
        var step = Assert.Single(run.Steps);
        Assert.True(step.Succeeded);
        Assert.Equal(VerificationOutcome.Inconclusive, step.Verification);
        Assert.NotNull(step.Error);
    }

    internal sealed class UnbuildableVerifier : IHealingVerifier
    {
        public UnbuildableVerifier() => throw new InvalidOperationException("The probe client is not configured.");

        public Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(VerificationOutcome.Healed);
    }

    [Fact]
    public async Task A_step_service_that_fails_to_dispose_does_not_turn_a_healed_run_into_a_failed_one()
    {
        await using var host = await HomeostatTestHost.StartAsync(
            h => h.AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                .With("renew", sp => sp.GetRequiredService<FailingDisposal>())
                .VerifyWith<HealedVerifier>())),
            services => services.AddScoped<FailingDisposal>());

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Single(run.Steps);
    }

    internal sealed class FailingDisposal : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => throw new InvalidOperationException("The client could not flush its buffer.");
    }

    internal sealed class HealedVerifier : IHealingVerifier
    {
        public Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(VerificationOutcome.Healed);
    }

    // ---------------------------------------------------------------- E10: detectors

    [Fact]
    public async Task A_slow_detector_does_not_delay_the_others()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.DetectionInterval = TimeSpan.FromSeconds(1))
            .AddDetector<SlowDetector>()
            .AddDetector<QuickDetector>()
            .AddCondition("custom-down", c => c.ForSource("custom")));

        try
        {
            // Before, the quick detector waited for the slow one (up to 30 s of real time) at every interval.
            await PollyControlTests.WaitUntilAsync(() => SlowDetector.Calls == 1 && QuickDetector.Calls >= 3);
            Assert.Equal(1, SlowDetector.Calls);
        }
        finally
        {
            SlowDetector.Release.TrySetResult();
        }
    }

    internal sealed class SlowDetector : ISignalDetector
    {
        private static int s_calls;   // created by the container: counted here (only one test uses it)

        public static TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static int Calls => Volatile.Read(ref s_calls);

        public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref s_calls);
            return new ValueTask(Release.Task);   // ignores its token
        }
    }

    internal sealed class QuickDetector : ISignalDetector
    {
        private static int s_calls;

        public static int Calls => Volatile.Read(ref s_calls);

        public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref s_calls);
            return ValueTask.CompletedTask;
        }
    }

    // ---------------------------------------------------------------- E12: metric thresholds

    [Fact]
    public void Metric_signal_thresholds_must_be_finite_and_ordered()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHomeostat(h => h.AddMetricSignal("x", m => m.UnhealthyAbove(double.NaN))));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHomeostat(h => h.AddMetricSignal("x", m => m.DegradedAbove(double.PositiveInfinity))));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHomeostat(h => h.AddMetricSignal("x", m => m.DegradedAbove(3).UnhealthyAbove(1))));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHomeostat(h => h.AddMetricSignal("x", m => m.DegradedBelow(1).UnhealthyBelow(3))));

        // Equal thresholds and one-sided ones stay valid.
        new ServiceCollection().AddHomeostat(h => h.AddMetricSignal("x", m => m.DegradedAbove(2).UnhealthyAbove(2).DegradedBelow(1)));
    }

    // ---------------------------------------------------------------- E14: dropped events

    [Fact]
    public void Events_dropped_from_a_full_sink_queue_are_counted_and_logged_at_most_once_a_minute()
    {
        var time = new FakeTimeProvider(T0);
        var logger = new CapturingLogger<EventSinkHost<SilentSink>>();
        var host = new EventSinkHost<SilentSink>(new SilentSink(), new EventSinkQueues(), Options.Create(new HomeostatOptions()), time, logger);
        HealingStepResult Step(int i) => new() { RunId = "r", ConditionName = "feed-down", Step = i, Name = "renew", Succeeded = true };

        // Not started: nothing reads the queue, so it fills up.
        for (var i = 0; i < EventSinkHost<SilentSink>.QueueSize + 5; i++)
        {
            host.OnStepCompleted(Step(i));
        }

        var first = Assert.Single(logger.Warnings);
        Assert.Contains("dropped 1 event", first);

        time.Advance(TimeSpan.FromSeconds(61));
        host.OnStepCompleted(Step(0));
        Assert.Equal(2, logger.Warnings.Count);
        Assert.Contains("dropped 5 event", logger.Warnings[1]);
    }

    internal sealed class SilentSink : IHomeostatEventSink
    {
        public Task WriteAsync(HomeostatEvent homeostatEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _warnings = [];

        public List<string> Warnings
        {
            get
            {
                lock (_warnings)
                {
                    return [.. _warnings];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                lock (_warnings)
                {
                    _warnings.Add(formatter(state, exception));
                }
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>An in-memory store whose document, plan state and history calls can be replaced.</summary>
    private sealed class ScriptedStore : IHomeostatStore
    {
        private readonly InMemoryHomeostatStore _inner = new();

        public bool Documents { get; init; }

        public bool History { get; init; }

        public Func<string, CancellationToken, Task<string?>>? GetDocument { get; init; }

        public Func<string, CancellationToken, Task>? SetDocument { get; init; }

        public Func<string, CancellationToken, Task<PlanState>>? GetPlanState { get; init; }

        public Func<ConditionEventRecord, CancellationToken, Task>? AppendConditionEvent { get; init; }

        public Func<int, CancellationToken, Task<IReadOnlyList<ConditionEventRecord>>>? GetConditionEvents { get; init; }

        private int _setDocumentCalls;

        public int SetDocumentCalls => Volatile.Read(ref _setDocumentCalls);

        public bool SupportsDocuments => Documents;

        public bool SupportsConditionHistory => History;

        public ValueTask<string?> GetDocumentAsync(string key, CancellationToken cancellationToken = default)
            => GetDocument is { } get ? new(get(key, cancellationToken)) : new((string?)null);

        public ValueTask SetDocumentAsync(string key, string document, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _setDocumentCalls);
            return SetDocument is { } set ? new(set(key, cancellationToken)) : ValueTask.CompletedTask;
        }

        public ValueTask AppendConditionEventAsync(ConditionEventRecord conditionEvent, int maxEvents, CancellationToken cancellationToken = default)
            => AppendConditionEvent is { } append ? new(append(conditionEvent, cancellationToken)) : ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<ConditionEventRecord>> GetConditionEventsAsync(int maxEvents, CancellationToken cancellationToken = default)
            => GetConditionEvents is { } get ? new(get(maxEvents, cancellationToken)) : ValueTask.FromResult<IReadOnlyList<ConditionEventRecord>>([]);

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default)
            => GetPlanState is { } get ? new(get(key, cancellationToken)) : _inner.GetPlanStateAsync(key, cancellationToken);

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) => _inner.UpdatePlanStateAsync(key, update, cancellationToken);

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken);

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => _inner.GetBudgetUsedAsync(now, window, cancellationToken);

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
}
