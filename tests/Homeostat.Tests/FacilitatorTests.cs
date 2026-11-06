using Homeostat.Calls;
using Homeostat.Conditions;
using Homeostat.Detectors;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Homeostat.Tests;

/// <summary>Starter presets and call signals: adopting Homeostat with less code.</summary>
public class FacilitatorTests
{
    [Fact]
    public async Task Web_api_defaults_register_observe_only_conditions_with_causes_before_symptoms()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddWebApiDefaults());

        var names = host.Control.GetConditions().Select(c => c.Name).Order(StringComparer.Ordinal);
        Assert.Equal(["exception-rate-high", "memory-pressure", "server-errors-high", "server-latency-high", "threadpool-starved"], names);

        // Observe only, and symptoms wait for their causes. (The real runtime detectors run in this host, so the
        // definitions are checked rather than a race between published and detected signals.)
        var conditions = host.Services.GetRequiredService<ConditionSet>();
        Assert.All(conditions.Trackers, t => Assert.Null(t.Definition.Plan));
        Assert.Equal(["threadpool-starved"], conditions.Get("server-errors-high")!.Definition.SuppressedBy);
        Assert.Equal(["threadpool-starved", "memory-pressure"], conditions.Get("server-latency-high")!.Definition.SuppressedBy);
        Assert.Equal(TimeSpan.FromMinutes(1), conditions.Get("server-errors-high")!.Definition.SustainFor);
    }

    [Fact]
    public async Task Worker_defaults_leave_out_the_server_conditions_and_take_plans_and_settings()
    {
        var renewed = 0;
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddWorkerDefaults(d =>
        {
            d.MemoryPressure.Enabled = false;
            d.ExceptionRate.SustainedFor = TimeSpan.Zero;
            d.ExceptionRate.Heal(p => p.With("recycle", _ => Interlocked.Increment(ref renewed)).UseVerifier<BuiltInActionTests.AlwaysHealed>());
        }));

        var names = host.Control.GetConditions().Select(c => c.Name).Order(StringComparer.Ordinal);
        Assert.Equal(["exception-rate-high", "threadpool-starved"], names);

        host.Publish("exceptions", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("exception-rate-high");
        Assert.Equal(1, renewed);
    }

    [Fact]
    public void Without_their_causes_the_server_conditions_are_not_suppressed_and_their_rule_can_change()
    {
        var services = new ServiceCollection();
        services.AddHomeostat(h => h.AddWebApiDefaults(d =>
        {
            d.ThreadPoolStarvation.Enabled = false;
            d.MemoryPressure.Enabled = false;
            d.ServerErrors.Configure(c => c.WhenValueAbove(0.10));
        }));
        var conditions = services.BuildServiceProvider().GetRequiredService<ConditionSet>();

        Assert.Empty(conditions.Get("server-errors-high")!.Definition.SuppressedBy);
        Assert.Empty(conditions.Get("server-latency-high")!.Definition.SuppressedBy);
        Assert.Null(conditions.Get("threadpool-starved"));
    }

    [Fact]
    public void Worker_defaults_publish_the_state_of_background_services_and_a_second_preset_is_refused()
    {
        var services = new ServiceCollection();
        services.AddHomeostat(h => h.AddWorkerDefaults());
        Assert.Contains(services.BuildServiceProvider().GetServices<ISignalDetector>(), d => d is BackgroundServiceSignalDetector);

        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHomeostat(h => h.AddWorkerDefaults().AddWebApiDefaults()));
        Assert.Contains("already registered", error.Message);
    }

    [Fact]
    public void Calls_without_a_duration_count_for_the_ratio_but_not_for_the_percentile()
    {
        var options = new CallSignalOptions { MinimumCalls = 2 };
        var recorder = new CallRecorder("geocoding:stream", options);
        options.MinimumCalls = 100;     // the recorder keeps its own copy
        recorder.Record(failed: true);
        recorder.Record(failed: false);

        var signal = recorder.Collect(DateTimeOffset.UtcNow, "calls")!;
        Assert.Equal(2, recorder.Options.MinimumCalls);
        Assert.Equal("2", signal.Tags!["calls"]);
        Assert.Equal("n/a", signal.Tags["p95_ms"]);
        Assert.Equal(SignalSeverity.Unhealthy, signal.Severity);
    }

    [Fact]
    public void Default_conditions_refuse_a_negative_duration()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddHomeostat(h => h.AddWebApiDefaults(d => d.ServerErrors.SustainedFor = TimeSpan.FromSeconds(-1))));

    [Fact]
    public async Task Call_signals_publish_the_failure_ratio_and_p95_of_each_interval()
    {
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddHomeostat(h => h.AddCallSignals("geocoding:api", o =>
        {
            o.MinimumCalls = 4;
            o.UnhealthyLatency = TimeSpan.FromSeconds(1);
        }));
        var provider = services.BuildServiceProvider();
        var recorder = provider.GetRequiredKeyedService<CallRecorder>("geocoding:api");
        var detector = provider.GetServices<ISignalDetector>().OfType<CallSignalDetector>().Single();

        recorder.Record(failed: false, TimeSpan.FromMilliseconds(100));
        recorder.Record(failed: false, TimeSpan.FromMilliseconds(120));
        recorder.Record(failed: true, TimeSpan.FromMilliseconds(90));
        recorder.Record(failed: false, TimeSpan.FromMilliseconds(110));
        var sink = new CollectingPublisher();
        await detector.DetectAsync(sink, CancellationToken.None);
        var signal = Assert.Single(sink.Signals);
        Assert.Equal("geocoding:api", signal.Source);
        Assert.Equal(0.25, signal.Value);
        Assert.Equal(SignalSeverity.Degraded, signal.Severity);
        Assert.Equal("120", signal.Tags!["p95_ms"]);

        // Slow calls make it Unhealthy even without failures; an empty interval publishes nothing.
        for (var i = 0; i < 4; i++)
        {
            recorder.Record(failed: false, TimeSpan.FromSeconds(2));
        }

        sink = new CollectingPublisher();
        await detector.DetectAsync(sink, CancellationToken.None);
        Assert.Equal(SignalSeverity.Unhealthy, Assert.Single(sink.Signals).Severity);

        sink = new CollectingPublisher();
        await detector.DetectAsync(sink, CancellationToken.None);
        Assert.Empty(sink.Signals);
    }

    [Fact]
    public async Task TrackAsync_records_failures_but_not_cancellations_by_the_caller()
    {
        var recorder = new CallRecorder("geocoding:api", new CallSignalOptions { MinimumCalls = 1 });
        Assert.Equal(42, await recorder.TrackAsync(_ => Task.FromResult(42)));
        await Assert.ThrowsAsync<TimeoutException>(() => recorder.TrackAsync(_ => Task.FromException<int>(new TimeoutException())));
        await Assert.ThrowsAsync<ArgumentException>(() => recorder.TrackAsync(_ => Task.FromException(new ArgumentException("bad address")), isFailure: ex => ex is not ArgumentException));

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => recorder.TrackAsync(ct => Task.Delay(1000, ct), cancellationToken: canceled.Token));

        var signal = recorder.Collect(DateTimeOffset.UtcNow, "calls")!;
        Assert.Equal("3", signal.Tags!["calls"]);
        Assert.Equal("1", signal.Tags["failures"]);
    }

    [Fact]
    public void Call_signal_options_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CallRecorder("x", new CallSignalOptions { DegradedFailureRatio = 0.6, UnhealthyFailureRatio = 0.5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CallRecorder("x", new CallSignalOptions { MinimumCalls = 0 }));
    }
}
