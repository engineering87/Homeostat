using FsCheck;
using FsCheck.Fluent;
using Homeostat.Calls;
using Homeostat.Conditions;
using Homeostat.Signals;

namespace Homeostat.Tests;

/// <summary>
/// Property-based tests (FsCheck): invariants of the arithmetic that turns calls and signal values into decisions,
/// checked against hundreds of generated inputs instead of a few hand-picked ones. A failure prints the smallest
/// counterexample FsCheck could shrink to.
/// </summary>
public class PropertyTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Config Runs = Config.QuickThrowOnFailure.WithMaxTest(300);

    // total calls, failures among them, minimum calls of the options
    private static readonly Arbitrary<(int Total, int Failures, int Minimum)> Intervals =
        (from total in Gen.Choose(0, 2_000)
         from failures in Gen.Choose(0, total)
         from minimum in Gen.Choose(1, 50)
         select (total, failures, minimum)).ToArbitrary();

    [Fact]
    public void A_call_interval_publishes_its_exact_failure_ratio_or_nothing()
        => Check.One(Runs, Prop.ForAll(Intervals, i =>
        {
            var options = new CallSignalOptions { MinimumCalls = i.Minimum };
            var signal = CallRecorder.Evaluate("calls:x", options, "calls", i.Total, i.Failures, null, Start);

            // Silent only without calls, or with failures among too few calls to judge.
            var silent = i.Total == 0 || (i.Total < i.Minimum && i.Failures > 0);
            if (signal is null)
            {
                return silent;
            }

            return !silent
                && signal.Value == (double)i.Failures / i.Total
                && signal.Value is >= 0 and <= 1
                && signal.Tags!["calls"] == i.Total.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && signal.Tags["failures"] == i.Failures.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }));

    [Fact]
    public void One_more_failure_never_makes_a_call_signal_healthier()
        => Check.One(Runs, Prop.ForAll(Intervals, i =>
        {
            if (i.Failures >= i.Total || i.Total < i.Minimum)
            {
                return true;
            }

            var options = new CallSignalOptions { MinimumCalls = i.Minimum };
            var before = CallRecorder.Evaluate("calls:x", options, "calls", i.Total, i.Failures, null, Start)!;
            var after = CallRecorder.Evaluate("calls:x", options, "calls", i.Total, i.Failures + 1, null, Start)!;
            return after.Severity >= before.Severity;
        }));

    [Fact]
    public void A_recorder_reports_every_call_once_and_a_p95_within_the_recorded_durations()
    {
        var calls = Gen.ListOf(Gen.Zip(Gen.Elements(true, false), Gen.Choose(1, 60_000))).ToArbitrary();
        Check.One(Runs, Prop.ForAll(calls, list =>
        {
            var recorder = new CallRecorder("calls:x", new CallSignalOptions { MinimumCalls = 1 });
            foreach (var (failed, ms) in list)
            {
                recorder.Record(failed, TimeSpan.FromMilliseconds(ms));
            }

            var signal = recorder.Collect(Start, "calls");
            var again = recorder.Collect(Start, "calls");
            if (list.Count == 0)
            {
                return signal is null && again is null;
            }

            var p95 = double.Parse(signal!.Tags!["p95_ms"], System.Globalization.CultureInfo.InvariantCulture);
            return again is null
                && signal.Tags["calls"] == list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && signal.Tags["failures"] == list.Count(c => c.Item1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                && p95 >= list.Min(c => c.Item2) && p95 <= list.Max(c => c.Item2);
        }));
    }

    [Fact]
    public void The_trend_of_values_on_a_straight_line_is_the_slope_of_that_line()
    {
        // intercept, slope per minute, number of points one minute apart
        var lines = (from intercept in Gen.Choose(-1_000_000, 1_000_000)
                     from slope in Gen.Choose(-10_000, 10_000)
                     from points in Gen.Choose(3, 120)
                     select (intercept, slope, points)).ToArbitrary();
        Check.One(Runs, Prop.ForAll(lines, l =>
        {
            var series = new SignalSeries(TimeSpan.FromMinutes(l.points));
            for (var t = 0; t < l.points; t++)
            {
                series.Add(new Signal("trend:x", SignalSeverity.Healthy, Start.AddMinutes(t)) { Value = l.intercept + ((double)l.slope * t) });
            }

            var measured = series.SlopePerMinute(minimum: 3);
            return measured is { } s && Math.Abs(s - l.slope) <= 1e-6 * (1 + Math.Abs(l.slope) + (Math.Abs(l.intercept) / l.points));
        }));
    }

    [Fact]
    public void A_series_keeps_the_values_inside_its_window_merging_only_those_closer_than_its_resolution()
    {
        // window in minutes, gaps between consecutive values in seconds: multiples of 30 s, so values land exactly on
        // the edge of the window often enough to check it
        var histories = (from window in Gen.Choose(1, 10)
                         from gaps in Gen.NonEmptyListOf(Gen.Choose(0, 20).Select(g => g * 30))
                         select (window, gaps)).ToArbitrary();
        Check.One(Runs, Prop.ForAll(histories, h =>
        {
            var window = TimeSpan.FromMinutes(h.window);
            var spacing = window / SignalSeries.MaxPoints;
            var series = new SignalSeries(window);
            var kept = new List<DateTimeOffset>();   // the model: values closer than window / MaxPoints replace the latest
            var at = Start;
            foreach (var gap in h.gaps)
            {
                at = at.AddSeconds(gap);
                if (kept.Count >= 2 && at - kept[^2] < spacing)
                {
                    kept[^1] = at;
                }
                else
                {
                    kept.Add(at);
                }

                kept.RemoveAll(t => t < at - window);
                series.Add(new Signal("window:x", SignalSeverity.Healthy, at) { Value = gap });
            }

            return series.Count == kept.Count && series.Latest == h.gaps[^1];
        }));
    }
}
