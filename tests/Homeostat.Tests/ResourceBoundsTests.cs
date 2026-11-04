using Homeostat.AspNetCore;
using Homeostat.Conditions;
using Homeostat.Metrics;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Homeostat.Tests;

/// <summary>
/// What Homeostat keeps in memory must not grow with the traffic, the signal rate or the cardinality of what it
/// observes: every structure that grows with its input has a limit.
/// </summary>
public class ResourceBoundsTests
{
    [Fact]
    public void A_window_keeps_a_bounded_number_of_points_whatever_the_signal_rate()
    {
        var series = new SignalSeries(TimeSpan.FromMinutes(5));
        var start = DateTimeOffset.Parse("2026-11-02T08:00:00Z");

        // A point every millisecond for 20 minutes, rising by 3 per minute: 300,000 points would be in the window.
        for (var i = 0; i < 1_200_000; i++)
        {
            var minutes = i / 60_000.0;
            series.Add(new Signal("queue", SignalSeverity.Healthy, start.AddMilliseconds(i)) { Value = 100 + (minutes * 3) });
        }

        Assert.True(series.Count <= SignalSeries.MaxPoints * 1.05, $"{series.Count} points kept");
        Assert.True(series.Count >= SignalSeries.MaxPoints / 2, $"only {series.Count} points kept");
        Assert.Equal(3, series.SlopePerMinute(5)!.Value, 3);
        Assert.Equal(100 + (1_199_999 / 60_000.0 * 3), series.Latest!.Value, 6);
    }

    [Fact]
    public void A_metric_with_a_high_cardinality_tag_tracks_a_bounded_number_of_series()
    {
        var watch = new MetricWatch("pool.connections", null, "pool", MetricStatistic.Last, [], null, null, null, null, null, minimumCount: 1);
        for (var i = 0; i < MetricWatch.MaxSeries * 2; i++)
        {
            watch.Record(1, null, InstrumentKind.ObservableUpDownCounter, $"user={i}");
        }

        var signal = watch.Collect(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(10))!;
        Assert.Equal(MetricWatch.MaxSeries, signal.Value);
        Assert.Contains("tag combinations", signal.Message);
    }

    [Fact]
    public void Alerts_keep_a_bounded_number_of_firing_series()
    {
        using var state = new AlertState(new AlertIngestionOptions(), new NullPublisher(), new FakeTimeProvider());
        var observations = new List<AlertObservation>();
        for (var source = 0; source < 30; source++)
        {
            for (var key = 0; key < 2_000; key++)
            {
                observations.Add(new AlertObservation($"alerts:{source}", $"series-{key}", true, SignalSeverity.Unhealthy, null, "firing", new Dictionary<string, string>()));
            }
        }

        var accepted = state.Apply(observations);
        Assert.Equal(AlertState.MaxFiringSeries, accepted);
    }

    [Fact]
    public async Task Memory_does_not_grow_with_the_number_of_signals()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.DefaultCooldown = TimeSpan.FromMinutes(1))
            .AddCondition("queue-rising", c => c.ForSource("queue").WhenRisingFasterThan(50, TimeSpan.FromMinutes(10)).Heal(p => p.With("drain", _ => { })))
            .AddCondition("latency-anomaly", c => c.ForSource("latency").WhenAnomalouslyHigh(4, TimeSpan.FromMinutes(30)).Heal(p => p.With("shed", _ => { })))
            .AddCondition("feed-flapping", c => c.ForSource("feed").Occurrences(3, TimeSpan.FromMinutes(5)).Heal(p => p.With("renew", _ => { })))
            .AddCondition("disk-full", c => c.ForSource("disk").WhenValueAbove(90).Heal(p => p.With("prune", _ => { }))));

        var random = new Random(3);
        var at = host.Time.GetUtcNow();
        void Publish(int count)
        {
            for (var i = 0; i < count; i++)
            {
                at = at.AddMilliseconds(250);
                host.Signals.TryPublish(new Signal("queue", SignalSeverity.Healthy, at) { Value = 1_000 + (random.NextDouble() * 50) });
                host.Signals.TryPublish(new Signal("latency", SignalSeverity.Healthy, at) { Value = 20 + random.NextDouble() + (i % 50_000 == 0 ? 500 : 0) });
                host.Signals.TryPublish(new Signal("feed", i % 997 < 3 ? SignalSeverity.Unhealthy : SignalSeverity.Healthy, at));
                host.Signals.TryPublish(new Signal("disk", SignalSeverity.Healthy, at) { Value = i % 20_000 < 10 ? 95 : 50 });
                if (i % 1_000 == 0)
                {
                    host.Time.SetUtcNow(at);
                    Thread.Yield();
                }
            }
        }

        static long Retained()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return GC.GetTotalMemory(forceFullCollection: true);
        }

        // Fill every window, then publish ten times as much: what stays in memory must not follow.
        Publish(100_000);
        await Task.Delay(500);
        var before = Retained();
        Publish(1_000_000);
        await Task.Delay(500);
        var after = Retained();

        var growth = after - before;
        Assert.True(growth < 16 * 1024 * 1024, $"retained memory grew by {growth / 1024} KB over a million signals per source");
    }

    private sealed class NullPublisher : ISignalPublisher
    {
        public bool TryPublish(Signal signal) => true;
    }
}
