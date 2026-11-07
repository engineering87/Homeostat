using System.Data.Common;
using Homeostat.Detectors;
using Homeostat.Probes;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Homeostat.Tests;

public class ProbeTests
{
    internal static (ServiceProvider Services, ISignalDetector Detector) Build(Action<HomeostatBuilder> configure, TimeProvider? time = null)
    {
        var services = new ServiceCollection().AddLogging();
        if (time is not null)
        {
            services.AddSingleton(time);
        }

        services.AddHomeostat(h =>
        {
            h.Configure(o => o.DetectionInterval = TimeSpan.FromSeconds(10));
            configure(h);
        });
        var provider = services.BuildServiceProvider();
        var detector = provider.GetServices<ISignalDetector>().OfType<ProbeDetector>().Single();
        return (provider, new AwaitingDetector(detector));
    }

    /// <summary>Starts the due probes, then waits until they have published, so tests can assert right after.</summary>
    private sealed class AwaitingDetector(ProbeDetector inner) : ISignalDetector
    {
        public async ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
        {
            await inner.DetectAsync(signals, cancellationToken);
            await inner.WhenIdleAsync();
        }
    }

    [Fact]
    public async Task Probe_values_are_classified_by_the_thresholds()
    {
        var values = new Queue<double>([5, 50, 500, 0.5]);
        var (services, detector) = Build(h => h.AddProbe("ingest-backlog", (_, _) => Task.FromResult(values.Dequeue()), p =>
        {
            p.DegradedAbove = 10;
            p.UnhealthyAbove = 100;
            p.DegradedBelow = 1;
        }));
        await using var _ = services;
        var signals = new CollectingPublisher();

        for (var i = 0; i < 4; i++)
        {
            await detector.DetectAsync(signals, CancellationToken.None);
        }

        Assert.Equal(
            [SignalSeverity.Healthy, SignalSeverity.Degraded, SignalSeverity.Unhealthy, SignalSeverity.Degraded],
            signals.Signals.Select(s => s.Severity));
        Assert.Equal([5d, 50, 500, 0.5], signals.Signals.Select(s => s.Value!.Value));
        Assert.All(signals.Signals, s =>
        {
            Assert.Equal("ingest-backlog", s.Source);
            Assert.Equal("probe", s.Tags!["origin"]);
            Assert.Equal("ok", s.Tags["outcome"]);
        });
    }

    [Fact]
    public async Task Failing_probe_publishes_the_failure_severity_without_a_value()
    {
        var (services, detector) = Build(h => h
            .AddProbe("archive-db", (_, _) => throw new FakeDbException(transient: true))
            .AddProbe("not-a-number", (_, _) => Task.FromResult(double.NaN), p => p.FailureSeverity = SignalSeverity.Degraded));
        await using var _ = services;
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);

        var failed = signals.Signals.Single(s => s.Source == "archive-db");
        Assert.Equal(SignalSeverity.Unhealthy, failed.Severity);
        Assert.Null(failed.Value);
        Assert.Equal("failed", failed.Tags!["outcome"]);
        Assert.Equal(nameof(FakeDbException), failed.Tags["error"]);
        Assert.Equal("true", failed.Tags["transient"]);

        var nan = signals.Signals.Single(s => s.Source == "not-a-number");
        Assert.Equal(SignalSeverity.Degraded, nan.Severity);
        Assert.Null(nan.Value);
    }

    [Fact]
    public async Task Hanging_probe_times_out_and_is_not_called_again_until_it_ends()
    {
        var calls = 0;
        var release = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (services, detector) = Build(h => h.AddProbe("search-index", (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return release.Task; // Ignores cancellation on purpose.
        }, p => p.Timeout = TimeSpan.FromMilliseconds(100)));
        await using var _ = services;
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);
        await detector.DetectAsync(signals, CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(["timeout", "still-running"], signals.Signals.Select(s => s.Tags!["outcome"]));
        Assert.All(signals.Signals, s => Assert.Equal(SignalSeverity.Unhealthy, s.Severity));

        release.SetResult(1);
        await Task.Delay(50);
        await detector.DetectAsync(signals, CancellationToken.None);

        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Probe_that_honors_cancellation_reports_a_timeout()
    {
        var (services, detector) = Build(h => h.AddProbe("slow", async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }, p => p.Timeout = TimeSpan.FromMilliseconds(50)));
        await using var _ = services;
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);

        Assert.Equal("timeout", signals.Signals.Single().Tags!["outcome"]);
    }

    [Fact]
    public async Task Probe_interval_skips_detection_ticks()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-01T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var calls = 0;
        var (services, detector) = Build(
            h => h.AddProbe("disk-free", (_, _) => Task.FromResult((double)Interlocked.Increment(ref calls)), p => p.Interval = TimeSpan.FromSeconds(30)),
            time);
        await using var _ = services;
        var signals = new CollectingPublisher();

        for (var tick = 0; tick < 7; tick++) // 0, 10, ..., 60 seconds, with a little jitter.
        {
            await detector.DetectAsync(signals, CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromMilliseconds(tick % 2 == 0 ? 20 : 0));
        }

        Assert.Equal(3, calls); // 0 s, ~30 s, ~60 s
    }

    [Fact]
    public async Task Latency_probe_publishes_the_elapsed_milliseconds()
    {
        var time = new FakeTimeProvider();
        var (services, detector) = Build(
            h => h.AddLatencyProbe("search-index", (_, _) =>
            {
                time.Advance(TimeSpan.FromMilliseconds(250));
                return Task.CompletedTask;
            }, p => p.DegradedAbove = 200),
            time);
        await using var _ = services;
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);

        var signal = signals.Signals.Single();
        Assert.Equal(250, signal.Value!.Value, precision: 3);
        Assert.Equal(SignalSeverity.Degraded, signal.Severity);
        Assert.Equal("latency-probe", signal.Tags!["origin"]);
    }

    [Fact]
    public async Task Probes_run_concurrently_and_in_their_own_scope()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        async Task<double> Wait(IServiceProvider sp, CancellationToken ct)
        {
            Assert.NotNull(sp.GetRequiredService<ScopedThing>());
            if (Interlocked.Increment(ref started) == 2)
            {
                gate.SetResult();
            }

            await gate.Task.WaitAsync(ct);
            return 1;
        }

        var (services, detector) = Build(h =>
        {
            h.Services.AddScoped<ScopedThing>();
            h.AddProbe("a", Wait, p => p.Timeout = TimeSpan.FromSeconds(5)).AddProbe("b", Wait, p => p.Timeout = TimeSpan.FromSeconds(5));
        });
        await using var _ = services;
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);

        Assert.Equal(2, signals.Signals.Count(s => s.Tags!["outcome"] == "ok"));
    }

    [Fact]
    public async Task Detect_returns_at_once_while_a_probe_is_slow()
    {
        var release = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection().AddLogging();
        services.AddHomeostat(h => h.AddProbe("slow", (_, _) => release.Task, p => p.Timeout = TimeSpan.FromSeconds(30)));
        await using var provider = services.BuildServiceProvider();
        var detector = provider.GetServices<ISignalDetector>().OfType<ProbeDetector>().Single();
        var signals = new CollectingPublisher();

        var detect = detector.DetectAsync(signals, CancellationToken.None);
        Assert.True(detect.IsCompleted);
        await detector.DetectAsync(signals, CancellationToken.None); // Still within its timeout: no new call, no signal.
        Assert.Empty(signals.Signals);

        release.SetResult(42);
        await detector.WhenIdleAsync();
        Assert.Equal(42, signals.Signals.Single().Value);
    }

    [Fact]
    public async Task Abandoned_call_is_canceled_and_cancellation_errors_count_as_timeouts()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (services, detector) = Build(h => h.AddProbe("archive-db", async (_, ct) =>
        {
            ct.Register(() => canceled.TrySetResult());
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                // Like SqlClient: cancellation surfaces as a provider exception.
                throw new FakeDbException(transient: true);
            }

            return 0;
        }, p => p.Timeout = TimeSpan.FromMilliseconds(50)));
        await using var _ = services;
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);

        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("timeout", signals.Signals.Single().Tags!["outcome"]);
    }

    [Fact]
    public async Task Database_failure_messages_do_not_carry_provider_details()
    {
        var (services, detector) = Build(h => h.AddProbe("archive-db", (_, _) => throw new FakeDbException(transient: false)));
        await using var _ = services;
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);

        Assert.False(signals.Signals.Single().Message!.Contains("connection reset", StringComparison.Ordinal));
    }

    [Fact]
    public void Probe_registration_is_validated()
    {
        var services = new ServiceCollection();
        services.AddHomeostat(h =>
        {
            h.AddProbe("queue", (_, _) => Task.FromResult(0d));
            Assert.Throws<InvalidOperationException>(() => h.AddProbe("queue", (_, _) => Task.FromResult(0d)));
            Assert.Throws<ArgumentException>(() => h.AddProbe("x", (_, _) => Task.FromResult(0d), p => { p.DegradedAbove = 10; p.UnhealthyAbove = 5; }));
            Assert.Throws<ArgumentException>(() => h.AddProbe("y", (_, _) => Task.FromResult(0d), p => p.Timeout = TimeSpan.Zero));
            Assert.Throws<ArgumentException>(() => h.AddProbe("z", (_, _) => Task.FromResult(0d), p => p.DegradedBelow = double.NaN));
            Assert.Throws<ArgumentException>(() => h.AddProbe("w", (_, _) => Task.FromResult(0d), p => p.FailureSeverity = (SignalSeverity)42));
            h.AddProbe("custom-origin", (_, _) => Task.FromResult(0d), p => p.Origin = "queue-depth");
        });
    }

    [Fact]
    public async Task Probe_signal_drives_a_condition_and_its_plan()
    {
        var depth = 50_000d;
        await using var host = await Homeostat.Testing.HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.DetectionInterval = TimeSpan.FromMilliseconds(20))
            .AddProbe("ingest-backlog", (_, _) => Task.FromResult(Volatile.Read(ref depth)), p => p.UnhealthyAbove = 10_000)
            .AddCondition("backlog-high", c => c.ForSource("ingest-backlog").Heal(p => p
                .With("scale-consumers", _ => Volatile.Write(ref depth, 10))
                .UseVerifier<BuiltInActionTests.AlwaysHealed>())));

        var run = await host.WaitForRunAsync("backlog-high");

        Assert.Equal(Healing.HealingRunOutcome.Healed, run.Outcome);
    }

    internal sealed class ScopedThing;

    internal sealed class FakeDbException(bool transient) : DbException("connection reset")
    {
        public override bool IsTransient => transient;
    }
}
