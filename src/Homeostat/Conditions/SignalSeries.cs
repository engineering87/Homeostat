using Homeostat.Signals;

namespace Homeostat.Conditions;

/// <summary>Values of the relevant signals within a time window, for trend and anomaly rules.</summary>
/// <remarks>
/// Used only from the engine loop (one signal at a time), so it needs no locking. Runs for every relevant signal, so it
/// does not allocate in steady state and does not scan the window: expired points are skipped and compacted in bulk,
/// and the mean, the deviation and the regression line come from running sums.
/// </remarks>
internal sealed class SignalSeries(TimeSpan window)
{
    private const int ExactRecomputeEvery = 4096;

    // Points from _start on are in the window; the ones before are expired and removed in bulk.
    private readonly List<(DateTimeOffset At, double Value)> _points = [];
    private int _start;

    // Running sums for the mean, the deviation and the regression line: y is the value minus _shift, x the minutes
    // since _origin. The shift starts as the first value and becomes the window's mean at each exact recomputation, and
    // the origin is the first point of the window then, so the sums stay small. The exact recomputation (every few
    // thousand points, when the origin falls a window behind, or when an outlier leaves) removes the rounding drift of
    // additions and subtractions.
    private double _shift;
    private DateTimeOffset _origin;
    private double _sum;
    private double _sumOfSquares;
    private double _sumX;
    private double _sumXX;
    private double _sumXY;
    private int _addsSinceExact;

    // The trigger and the clear rule ask for the same figure for the same signal: compute it once per change.
    private long _version;
    private (long Version, int Minimum, (double Slope, double Intercept)? Line) _regression = (-1, 0, null);

    public int Count => _points.Count - _start;

    public void Add(Signal signal)
    {
        if (signal.Value is not { } value || double.IsNaN(value) || double.IsInfinity(value))
        {
            return;
        }

        if (Count == 0)
        {
            _points.Clear();
            _start = 0;
            _shift = value;
            _origin = signal.Timestamp;
            _sum = _sumOfSquares = _sumX = _sumXX = _sumXY = 0;
        }

        _points.Add((signal.Timestamp, value));
        Accumulate(_points[^1], 1);
        _version++;

        var cutoff = signal.Timestamp - window;
        var largestExpired = 0d;
        while (_start < _points.Count && _points[_start].At < cutoff)
        {
            Accumulate(_points[_start], -1);
            largestExpired = Math.Max(largestExpired, Math.Abs(_points[_start].Value - _shift));
            _start++;
        }

        // Subtracting an outlier far larger than the values left behind loses their precision: recompute exactly.
        var outlierLeft = largestExpired * largestExpired > 1e6 * Math.Max(_sumOfSquares / Count, double.Epsilon);

        if (_start > 64 && _start >= _points.Count / 2)
        {
            _points.RemoveRange(0, _start);
            _start = 0;
        }

        if (++_addsSinceExact >= ExactRecomputeEvery || outlierLeft || _points[_start].At - _origin > window)
        {
            RecomputeSums();
        }
    }

    /// <summary>
    /// Least-squares slope of the values, per minute; <c>null</c> with fewer than <paramref name="minimum"/> points or
    /// when they span less than half the window, so a short burst (for example at startup) is not extrapolated.
    /// </summary>
    public double? SlopePerMinute(int minimum) => Regression(minimum)?.Slope;

    /// <summary>The latest value, if any.</summary>
    public double? Latest => Count == 0 ? null : _points[^1].Value;

    /// <summary>
    /// Least-squares slope per minute of the latest <paramref name="count"/> values only: the direction right now,
    /// without the older history that a fix has made irrelevant. <c>null</c> with fewer values or no time span.
    /// </summary>
    public double? RecentSlopePerMinute(int count)
    {
        if (Count < Math.Max(count, 2))
        {
            return null;
        }

        return Fit(_points.Count - count, _points.Count)?.Slope;
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

        var x = (_points[^1].At - _points[_start].At + horizon).TotalMinutes;
        return line.Intercept + (line.Slope * x);
    }

    /// <summary>
    /// How many standard deviations the latest value is from the mean of the earlier ones; <c>null</c> without
    /// enough history. The deviation has a floor of 1% of the mean, so a perfectly flat history does not make every
    /// tiny change an anomaly.
    /// </summary>
    public double? LatestZScore(int minimumHistory)
    {
        var length = Count - 1;
        if (length < minimumHistory || length <= 0)
        {
            return null;
        }

        // The history is every point but the latest.
        var latest = _points[^1].Value - _shift;
        var sum = _sum - latest;
        var shiftedMean = sum / length;
        var variance = Math.Max(0, ((_sumOfSquares - (latest * latest)) / length) - (shiftedMean * shiftedMean));
        var mean = shiftedMean + _shift;
        var deviation = Math.Max(Math.Sqrt(variance), Math.Max(Math.Abs(mean) * 0.01, 1e-9));
        return (_points[^1].Value - mean) / deviation;
    }

    /// <summary>Least-squares line of the values against minutes since the first point.</summary>
    private (double Slope, double Intercept)? Regression(int minimum)
    {
        if (_regression.Version == _version && _regression.Minimum == minimum)
        {
            return _regression.Line;
        }

        (double Slope, double Intercept)? line = null;
        if (Count >= minimum && Count >= 2 && _points[^1].At - _points[_start].At >= window / 2)
        {
            double n = Count;
            var denominator = n * _sumXX - _sumX * _sumX;
            if (denominator > 1e-12)
            {
                var slope = (n * _sumXY - _sumX * _sum) / denominator;

                // The intercept is the line's value at the first point of the window.
                var firstX = (_points[_start].At - _origin).TotalMinutes;
                line = (slope, _shift + (_sum / n) - (slope * ((_sumX / n) - firstX)));
            }
        }

        _regression = (_version, minimum, line);
        return line;
    }

    private void Accumulate((DateTimeOffset At, double Value) point, int sign)
    {
        var x = (point.At - _origin).TotalMinutes;
        var y = point.Value - _shift;
        _sum += sign * y;
        _sumOfSquares += sign * y * y;
        _sumX += sign * x;
        _sumXX += sign * x * x;
        _sumXY += sign * x * y;
    }

    /// <summary>Least-squares line of the points from <paramref name="first"/> to <paramref name="end"/> (excluded).</summary>
    private (double Slope, double Intercept)? Fit(int first, int end)
    {
        // Centered on the first point, so large values keep their precision.
        var (origin, offset) = _points[first];
        double n = end - first, sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (var i = first; i < end; i++)
        {
            var (at, raw) = _points[i];
            var value = raw - offset;
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
        return (slope, offset + ((sumY - slope * sumX) / n));
    }

    private void RecomputeSums()
    {
        _addsSinceExact = 0;
        _sum = _sumOfSquares = _sumX = _sumXX = _sumXY = 0;
        if (Count == 0)
        {
            return;
        }

        // Shifted by the mean of the window, the sums of squares stay as small as they can be.
        var total = 0d;
        for (var i = _start; i < _points.Count; i++)
        {
            total += _points[i].Value;
        }

        _shift = total / Count;
        _origin = _points[_start].At;
        for (var i = _start; i < _points.Count; i++)
        {
            Accumulate(_points[i], 1);
        }
    }
}
