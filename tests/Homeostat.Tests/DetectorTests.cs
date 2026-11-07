using Homeostat.Detectors;
using Homeostat.Healing;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Homeostat.Tests;

public class DetectorTests
{
    [Fact]
    public async Task ThreadPool_detector_publishes_a_signal_with_latency()
    {
        var sink = new CollectingPublisher();
        var detector = new ThreadPoolStarvationDetector(Options.Create(new ThreadPoolStarvationDetectorOptions()), TimeProvider.System);

        await detector.DetectAsync(sink, CancellationToken.None);

        var signal = Assert.Single(sink.Signals);
        Assert.Equal("threadpool", signal.Source);
        Assert.True(signal.Tags!.ContainsKey("latency_ms"));
    }

    [Fact]
    public async Task Memory_detector_publishes_a_signal_after_a_collection()
    {
        GC.Collect();
        var sink = new CollectingPublisher();
        var detector = new MemoryPressureDetector(Options.Create(new MemoryPressureDetectorOptions()), TimeProvider.System);

        await detector.DetectAsync(sink, CancellationToken.None);

        var signal = Assert.Single(sink.Signals);
        Assert.Equal("memory", signal.Source);
        Assert.True(signal.Tags!.ContainsKey("load"));
    }

    [Fact]
    public async Task Exception_rate_detector_reports_the_rate_since_the_previous_sample()
    {
        var time = new FakeTimeProvider();
        var options = new ExceptionRateDetectorOptions { DegradedPerSecond = 1, UnhealthyPerSecond = 1_000_000 };
        using var detector = new ExceptionRateDetector(Options.Create(options), time);
        var sink = new CollectingPublisher();

        for (var i = 0; i < 5; i++)
        {
            try
            {
                throw new InvalidOperationException("Test exception.");
            }
            catch (InvalidOperationException)
            {
                // Counted by the detector.
            }
        }

        time.Advance(TimeSpan.FromSeconds(1));
        await detector.DetectAsync(sink, CancellationToken.None);

        // Other tests running in parallel can only add exceptions, never remove them.
        Assert.Equal(SignalSeverity.Degraded, Assert.Single(sink.Signals).Severity);
    }

    [Fact]
    public async Task Custom_detectors_feed_conditions()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddDetector<AlwaysUnhealthyDetector>()
            .AddCondition("custom-down", c => c
                .ForSource("custom")
                .Heal(p => p.With("noop", _ => { }).UseVerifier<EngineTests.AlwaysHealedVerifier>())));

        var run = await host.WaitForRunAsync("custom-down");

        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
    }

    internal sealed class AlwaysUnhealthyDetector : ISignalDetector
    {
        public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
        {
            signals.TryPublish("custom", SignalSeverity.Unhealthy);
            return ValueTask.CompletedTask;
        }
    }
}
