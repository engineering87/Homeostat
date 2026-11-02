using Homeostat.Conditions;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Tests;

/// <summary>The per-signal code paths, made cheaper for 1.0, still give the same answers.</summary>
public class HotPathTests
{
    private static ConditionDefinition Define(string name, Action<ConditionBuilder> configure)
    {
        var builder = new ConditionBuilder(name, new ServiceCollection());
        configure(builder);
        return builder.Build();
    }

    [Fact]
    public void Signals_reach_the_conditions_of_their_source_and_those_without_a_source_in_registration_order()
    {
        var set = new ConditionSet(
        [
            Define("feed-a", c => c.ForSource("feed")),
            Define("any-critical", c => c.Where(s => s.Message == "critical").ClearWhen(_ => false)),
            Define("queue", c => c.ForSource("queue")),
            Define("feed-b", c => c.ForSource("feed").Where(s => s.Value > 1)),
        ]);

        Assert.Equal(["feed-a", "any-critical", "feed-b"], set.SignalTrackersFor("feed").Select(t => t.Definition.Name));
        Assert.Equal(["any-critical", "queue"], set.SignalTrackersFor("queue").Select(t => t.Definition.Name));
        Assert.Equal(["any-critical"], set.SignalTrackersFor("unknown").Select(t => t.Definition.Name));
    }

    [Fact]
    public void Replacing_the_conditions_rebuilds_the_source_index()
    {
        var set = new ConditionSet([Define("feed", c => c.ForSource("feed"))]);
        set.Replace([Define("feed", c => c.ForSource("feed-v2"))]);

        Assert.Empty(set.SignalTrackersFor("feed"));
        Assert.Equal(["feed"], set.SignalTrackersFor("feed-v2").Select(t => t.Definition.Name));
    }

    [Fact]
    public void Running_sums_give_the_exact_z_score_and_line_over_a_long_sliding_window()
    {
        var series = new SignalSeries(TimeSpan.FromMinutes(1));
        var values = new List<(DateTimeOffset At, double Value)>();
        var start = DateTimeOffset.Parse("2026-11-02T08:00:00Z");
        var random = new Random(42);

        // A level far from zero with real spread (above the 1% floor), and spikes, over a window that slides 20 times.
        for (var i = 0; i < 20_000; i++)
        {
            var at = start.AddMilliseconds(i * 250);
            var value = 100_000 + (random.NextDouble() * 5_000) + (i % 500 == 0 ? 20_000 : 0);
            series.Add(new Signal("latency", SignalSeverity.Healthy, at) { Value = value });
            values.Add((at, value));

            if ((i > 0 && i % 997 == 0) || i == 19_999)
            {
                var window = values.Where(p => p.At >= at - TimeSpan.FromMinutes(1)).Select(p => p.Value).ToArray();
                Assert.Equal(window.Length, series.Count);
                var history = window[..^1];
                var mean = history.Average();
                var deviation = Math.Max(Math.Sqrt(history.Sum(v => (v - mean) * (v - mean)) / history.Length), Math.Abs(mean) * 0.01);
                var expected = (window[^1] - mean) / deviation;
                Assert.True(Math.Abs(series.LatestZScore(2)!.Value - expected) < 1e-9 * Math.Max(1, Math.Abs(expected)), $"at {i}: {series.LatestZScore(2)} vs {expected}");

                // The regression line, computed directly over the same window.
                var points = values.Where(p => p.At >= at - TimeSpan.FromMinutes(1)).ToArray();
                var xs = points.Select(p => (p.At - points[0].At).TotalMinutes).ToArray();
                double n = xs.Length, sx = xs.Sum(), sy = points.Sum(p => p.Value);
                double sxx = xs.Sum(x => x * x), sxy = xs.Zip(points, (x, p) => x * p.Value).Sum();
                var slope = ((n * sxy) - (sx * sy)) / ((n * sxx) - (sx * sx));
                var intercept = (sy - (slope * sx)) / n;
                var projected = intercept + (slope * (xs[^1] + 2));
                Assert.True(Math.Abs(series.SlopePerMinute(5)!.Value - slope) < 1e-6 * Math.Max(1, Math.Abs(slope)), $"slope at {i}: {series.SlopePerMinute(5)} vs {slope}");
                Assert.True(Math.Abs(series.ProjectedValue(5, TimeSpan.FromMinutes(2))!.Value - projected) < 1e-6 * Math.Abs(projected), $"projection at {i}");
            }
        }
    }

    [Fact]
    public void The_slope_and_projection_follow_the_window_after_compaction()
    {
        var series = new SignalSeries(TimeSpan.FromMinutes(10));
        var start = DateTimeOffset.Parse("2026-11-02T08:00:00Z");
        for (var i = 0; i < 5_000; i++)
        {
            // Flat for a long time, then rising by 2 per minute: only the rise is in the last ten minutes.
            var minutes = i * 0.05;
            var value = minutes < 200 ? 50 : 50 + ((minutes - 200) * 2);
            series.Add(new Signal("queue", SignalSeverity.Healthy, start.AddMinutes(minutes)) { Value = value });
        }

        Assert.Equal(2, series.SlopePerMinute(5)!.Value, 6);
        Assert.Equal(2, series.RecentSlopePerMinute(10)!.Value, 6);
        Assert.Equal(series.Latest!.Value + 20, series.ProjectedValue(5, TimeSpan.FromMinutes(10))!.Value, 6);
    }

    [Fact]
    public void An_extreme_outlier_leaving_the_window_does_not_spoil_the_z_score()
    {
        var series = new SignalSeries(TimeSpan.FromMinutes(1));
        var values = new List<(DateTimeOffset At, double Value)>();
        var start = DateTimeOffset.Parse("2026-11-02T08:00:00Z");
        var random = new Random(7);
        for (var i = 0; i < 1_000; i++)
        {
            var at = start.AddMilliseconds(i * 250);
            var value = i == 10 ? 1e12 : 1_000 + (random.NextDouble() * 100);
            series.Add(new Signal("latency", SignalSeverity.Healthy, at) { Value = value });
            values.Add((at, value));
        }

        // The spike left the window long ago, well before an exact recomputation would be due by count.
        var window = values.Where(p => p.At >= values[^1].At - TimeSpan.FromMinutes(1)).Select(p => p.Value).ToArray();
        var history = window[..^1];
        var mean = history.Average();
        var deviation = Math.Max(Math.Sqrt(history.Sum(v => (v - mean) * (v - mean)) / history.Length), Math.Abs(mean) * 0.01);
        Assert.True(Math.Abs(series.LatestZScore(2)!.Value - ((window[^1] - mean) / deviation)) < 1e-9);
    }
}
