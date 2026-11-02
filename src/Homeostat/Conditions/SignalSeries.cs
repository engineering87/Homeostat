using Homeostat.Signals;

namespace Homeostat.Conditions;

/// <summary>Values of the relevant signals within a time window, for trend and anomaly rules.</summary>
/// <remarks>Used only from the engine loop (one signal at a time), so it needs no locking.</remarks>
internal sealed class SignalSeries(TimeSpan window)
{
    private readonly List<(DateTimeOffset At, double Value)> _points = [];

    public int Count => _points.Count;

    public void Add(Signal signal)
    {
        if (signal.Value is not { } value || double.IsNaN(value) || double.IsInfinity(value))
        {
            return;
        }

        _points.Add((signal.Timestamp, value));
        var cutoff = signal.Timestamp - window;
        var expired = _points.FindIndex(p => p.At >= cutoff);
        if (expired > 0)
        {
            _points.RemoveRange(0, expired);
        }
        else if (expired < 0)
        {
            _points.Clear();
        }
    }

    /// <summary>
    /// Least-squares slope of the values, per minute; <c>null</c> with fewer than <paramref name="minimum"/> points or
    /// when they span less than half the window, so a short burst (for example at startup) is not extrapolated.
    /// </summary>
    public double? SlopePerMinute(int minimum) => Regression(minimum)?.Slope;

    /// <summary>The latest value, if any.</summary>
    public double? Latest => _points.Count == 0 ? null : _points[^1].Value;

    /// <summary>
    /// Least-squares slope per minute of the latest <paramref name="count"/> values only: the direction right now,
    /// without the older history that a fix has made irrelevant. <c>null</c> with fewer values or no time span.
    /// </summary>
    public double? RecentSlopePerMinute(int count)
    {
        if (_points.Count < Math.Max(count, 2))
        {
            return null;
        }

        var recent = _points.Skip(_points.Count - count).ToList();
        var origin = recent[0].At;
        double n = recent.Count, sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        foreach (var (at, value) in recent)
        {
            var x = (at - origin).TotalMinutes;
            sumX += x;
            sumY += value;
            sumXY += x * value;
            sumXX += x * x;
        }

        var denominator = n * sumXX - sumX * sumX;
        return denominator <= 1e-12 ? null : (n * sumXY - sumX * sumY) / denominator;
    }

    /// <summary>
    /// The value the regression line reaches <paramref name="horizon"/> after the latest point; <c>null</c> under the
    /// same conditions as <see cref="SlopePerMinute"/>.
    /// </summary>
    public double? ProjectedValue(int minimum, TimeSpan horizon)
    {
        if (Regression(minimum) is not { } line)
        {
            return null;
        }

        var x = (_points[^1].At - _points[0].At + horizon).TotalMinutes;
        return line.Intercept + (line.Slope * x);
    }

    /// <summary>Least-squares line of the values against minutes since the first point.</summary>
    private (double Slope, double Intercept)? Regression(int minimum)
    {
        if (_points.Count < minimum || _points.Count < 2 || _points[^1].At - _points[0].At < window / 2)
        {
            return null;
        }

        var origin = _points[0].At;
        double n = _points.Count, sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        foreach (var (at, value) in _points)
        {
            var x = (at - origin).TotalMinutes;
            sumX += x;
            sumY += value;
            sumXY += x * value;
            sumXX += x * x;
        }

        var denominator = n * sumXX - sumX * sumX;
        if (denominator <= 1e-12)
        {
            return null;
        }

        var slope = (n * sumXY - sumX * sumY) / denominator;
        return (slope, (sumY - slope * sumX) / n);
    }

    /// <summary>
    /// How many standard deviations the latest value is from the mean of the earlier ones; <c>null</c> without
    /// enough history. The deviation has a floor of 1% of the mean, so a perfectly flat history does not make every
    /// tiny change an anomaly.
    /// </summary>
    public double? LatestZScore(int minimumHistory)
    {
        if (_points.Count - 1 < minimumHistory)
        {
            return null;
        }

        var history = _points.Take(_points.Count - 1).Select(p => p.Value).ToArray();
        var mean = history.Average();
        var variance = history.Sum(v => (v - mean) * (v - mean)) / history.Length;
        var deviation = Math.Max(Math.Sqrt(variance), Math.Max(Math.Abs(mean) * 0.01, 1e-9));
        return (_points[^1].Value - mean) / deviation;
    }
}
