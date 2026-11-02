using System.Globalization;
using Homeostat.Signals;

namespace Homeostat.Metrics;

/// <summary>
/// Turns one <see cref="System.Diagnostics.Metrics"/> instrument (the API under OpenTelemetry metrics) into a
/// signal: at every detection interval a statistic of the measurements is compared with thresholds.
/// </summary>
/// <example>
/// <code>
/// .AddMetricSignal("http.client.request.duration", m => m
///     .Source("forecast-latency")
///     .Where("server.address", "forecast.example")
///     .Statistic(MetricStatistic.P95)
///     .DegradedAbove(1)       // seconds
///     .UnhealthyAbove(3))
/// </code>
/// </example>
public sealed class MetricSignalBuilder
{
    private readonly List<Func<IReadOnlyDictionary<string, object?>, bool>> _filters = [];
    private string? _meter;
    private string? _source;
    private MetricStatistic _statistic = MetricStatistic.Mean;
    private Func<double, IReadOnlyDictionary<string, object?>, bool>? _ratioMatch;
    private double? _degradedAbove;
    private double? _unhealthyAbove;
    private double? _degradedBelow;
    private double? _unhealthyBelow;
    private int _minimumCount = 1;
    private double _scale = 1;

    internal MetricSignalBuilder(string instrument) => Instrument = instrument;

    internal string Instrument { get; }

    /// <summary>Only listens to the instrument in the meter with this name (by default, any meter).</summary>
    /// <param name="meterName">Meter name, for example <c>"Microsoft.AspNetCore.Hosting"</c>.</param>
    public MetricSignalBuilder FromMeter(string meterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meterName);
        _meter = meterName;
        return this;
    }

    /// <summary>Signal source; by default <c>"metric:{instrument}"</c>.</summary>
    /// <param name="source">Source name used by conditions.</param>
    public MetricSignalBuilder Source(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        _source = source;
        return this;
    }

    /// <summary>Only counts measurements whose tag equals the value (compared as invariant text).</summary>
    /// <param name="tag">Tag name, for example <c>"server.address"</c>.</param>
    /// <param name="value">Expected value.</param>
    public MetricSignalBuilder Where(string tag, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var expected = Text(value);
        _filters.Add(tags => tags.TryGetValue(tag, out var actual) && Text(actual) == expected);
        return this;
    }

    /// <summary>Only counts measurements whose tags satisfy the predicate.</summary>
    /// <param name="predicate">Receives the tags of a measurement.</param>
    public MetricSignalBuilder Where(Func<IReadOnlyDictionary<string, object?>, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _filters.Add(predicate);
        return this;
    }

    /// <summary>Statistic compared with the thresholds (default <see cref="MetricStatistic.Mean"/>).</summary>
    /// <param name="statistic">The statistic.</param>
    public MetricSignalBuilder Statistic(MetricStatistic statistic)
    {
        _statistic = statistic;
        return this;
    }

    /// <summary>
    /// Uses <see cref="MetricStatistic.Ratio"/>: the fraction of measurements that match, for example the share of
    /// requests that failed.
    /// </summary>
    /// <param name="match">Receives the measured value and the tags.</param>
    public MetricSignalBuilder RatioOf(Func<double, IReadOnlyDictionary<string, object?>, bool> match)
    {
        _ratioMatch = match ?? throw new ArgumentNullException(nameof(match));
        _statistic = MetricStatistic.Ratio;
        return this;
    }

    /// <summary>The signal is <see cref="SignalSeverity.Degraded"/> when the statistic is at or above the value.</summary>
    /// <param name="value">Threshold, in the unit of the instrument.</param>
    public MetricSignalBuilder DegradedAbove(double value)
    {
        _degradedAbove = value;
        return this;
    }

    /// <summary>The signal is <see cref="SignalSeverity.Unhealthy"/> when the statistic is at or above the value.</summary>
    /// <param name="value">Threshold, in the unit of the instrument.</param>
    public MetricSignalBuilder UnhealthyAbove(double value)
    {
        _unhealthyAbove = value;
        return this;
    }

    /// <summary>The signal is <see cref="SignalSeverity.Degraded"/> when the statistic is at or below the value.</summary>
    /// <param name="value">Threshold, in the unit of the instrument.</param>
    public MetricSignalBuilder DegradedBelow(double value)
    {
        _degradedBelow = value;
        return this;
    }

    /// <summary>The signal is <see cref="SignalSeverity.Unhealthy"/> when the statistic is at or below the value.</summary>
    /// <param name="value">Threshold, in the unit of the instrument.</param>
    public MetricSignalBuilder UnhealthyBelow(double value)
    {
        _unhealthyBelow = value;
        return this;
    }

    /// <summary>
    /// Multiplies the statistic before it is compared with the thresholds and published, for example
    /// <c>Scale(1.0 / maxPoolSize)</c> to turn a count into a share. Defaults to 1.
    /// </summary>
    /// <param name="factor">A finite number other than zero.</param>
    public MetricSignalBuilder Scale(double factor)
    {
        if (!double.IsFinite(factor) || factor == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), "The factor must be a finite number other than zero.");
        }

        _scale = factor;
        return this;
    }

    /// <summary>No signal is published for an interval with fewer measurements (default 1).</summary>
    /// <param name="count">Minimum number of measurements.</param>
    public MetricSignalBuilder MinimumCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        _minimumCount = count;
        return this;
    }

    internal MetricWatch Build()
    {
        if (_statistic == MetricStatistic.Ratio && _ratioMatch is null)
        {
            throw new InvalidOperationException($"The metric signal for '{Instrument}' uses Ratio: call RatioOf(...) to say what to count.");
        }

        if (_degradedAbove is null && _unhealthyAbove is null && _degradedBelow is null && _unhealthyBelow is null)
        {
            throw new InvalidOperationException($"The metric signal for '{Instrument}' has no threshold: call DegradedAbove, UnhealthyAbove, DegradedBelow or UnhealthyBelow.");
        }

        return new MetricWatch(
            Instrument, _meter, _source ?? $"metric:{Instrument}", _statistic, _filters.ToArray(), _ratioMatch,
            _degradedAbove, _unhealthyAbove, _degradedBelow, _unhealthyBelow, _minimumCount, _scale);
    }

    internal static string? Text(object? value) => value switch
    {
        null => null,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
