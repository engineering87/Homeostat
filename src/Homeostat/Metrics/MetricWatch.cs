using System.Globalization;
using Homeostat.Signals;

namespace Homeostat.Metrics;

/// <summary>One watched instrument and the measurements of the current interval.</summary>
internal sealed class MetricWatch
{
    internal const int MaxSamples = 4096;

    // Collections an observable series may miss before it is forgotten (its pool was disposed, a tag value changed).
    internal const int MaxMissedCollections = 10;

    // Series (tag combinations) tracked per instrument at most: an instrument with a high-cardinality tag (a user,
    // a URL) must not grow memory without limit. Further series are ignored until others are forgotten.
    internal const int MaxSeries = 10_000;

    private readonly object _gate = new();
    private readonly List<double> _samples = [];
    private long _count;
    private long _matches;
    private double _sum;
    private double _min = double.PositiveInfinity;
    private double _max = double.NegativeInfinity;
    private double _last;

    // Level of an up-down counter since the start (changes summed), and latest value of each observable series.
    private double _level;
    private readonly Dictionary<string, Series> _series = new(StringComparer.Ordinal);
    private InstrumentKind _kind;

    public MetricWatch(
        string instrument,
        string? meter,
        string source,
        MetricStatistic statistic,
        Func<IReadOnlyDictionary<string, object?>, bool>[] filters,
        Func<double, IReadOnlyDictionary<string, object?>, bool>? ratioMatch,
        double? degradedAbove,
        double? unhealthyAbove,
        double? degradedBelow,
        double? unhealthyBelow,
        int minimumCount,
        double scale = 1)
    {
        Scale = scale;
        Instrument = instrument;
        Meter = meter;
        Source = source;
        Statistic = statistic;
        Filters = filters;
        RatioMatch = ratioMatch;
        DegradedAbove = degradedAbove;
        UnhealthyAbove = unhealthyAbove;
        DegradedBelow = degradedBelow;
        UnhealthyBelow = unhealthyBelow;
        MinimumCount = minimumCount;
    }

    public string Instrument { get; }

    /// <summary>Factor applied to the statistic before it is compared and published.</summary>
    public double Scale { get; }

    public string? Meter { get; }

    public string Source { get; }

    public MetricStatistic Statistic { get; }

    public Func<IReadOnlyDictionary<string, object?>, bool>[] Filters { get; }

    public Func<double, IReadOnlyDictionary<string, object?>, bool>? RatioMatch { get; }

    public double? DegradedAbove { get; }

    public double? UnhealthyAbove { get; }

    public double? DegradedBelow { get; }

    public double? UnhealthyBelow { get; }

    public int MinimumCount { get; }

    private bool KeepsSamples => Statistic is MetricStatistic.P50 or MetricStatistic.P90 or MetricStatistic.P95 or MetricStatistic.P99;

    /// <summary>Whether recording needs the tags as a dictionary.</summary>
    public bool NeedsTags => Filters.Length > 0 || RatioMatch is not null;

    public bool Matches(string meterName, string instrumentName)
        => string.Equals(instrumentName, Instrument, StringComparison.Ordinal)
            && (Meter is null || string.Equals(meterName, Meter, StringComparison.Ordinal));

    public void Record(double value, IReadOnlyDictionary<string, object?>? tags, InstrumentKind kind = InstrumentKind.Other, string? series = null)
    {
        if (tags is not null)
        {
            foreach (var filter in Filters)
            {
                if (!filter(tags))
                {
                    return;
                }
            }
        }

        // Kept per kind (an up-down level, observable series) only for those kinds: the common case, a plain counter or a
        // histogram, takes one lock per measurement.
        if (kind != InstrumentKind.Other)
        {
            lock (_gate)
            {
                _kind = kind;
                switch (kind)
                {
                    case InstrumentKind.UpDownCounter:
                        _level += value;
                        break;

                    case InstrumentKind.ObservableCounter:
                    {
                        // Running total per series: count only the increment since the previous observation.
                        var previous = Observe(series, value);
                        value = value >= previous ? value - previous : value;   // a smaller total means a reset
                        break;
                    }

                    case InstrumentKind.ObservableUpDownCounter:
                        Observe(series, value);
                        break;
                }
            }
        }

        var matched = RatioMatch is not null && tags is not null && RatioMatch(value, tags);

        lock (_gate)
        {
            _count++;
            _sum += value;
            _last = value;
            if (value < _min)
            {
                _min = value;
            }

            if (value > _max)
            {
                _max = value;
            }

            if (matched)
            {
                _matches++;
            }

            // Only percentiles need the values themselves.
            if (!KeepsSamples)
            {
                return;
            }

            if (_samples.Count < MaxSamples)
            {
                _samples.Add(value);
            }
            else
            {
                // Reservoir sampling keeps percentiles representative of the whole interval.
                var slot = Random.Shared.NextInt64(_count);
                if (slot < MaxSamples)
                {
                    _samples[(int)slot] = value;
                }
            }
        }
    }

    /// <summary>Computes the statistic of the interval, resets it, and returns a signal (or <c>null</c> if too few measurements).</summary>
    public Signal? Collect(DateTimeOffset now, TimeSpan elapsed)
    {
        long count;
        long matches;
        double sum;
        double min;
        double max;
        double last;
        double[] samples;
        double level;
        InstrumentKind kind;
        bool seriesDropped;

        lock (_gate)
        {
            level = _level;
            kind = _kind;
            seriesDropped = SeriesDropped;
            SeriesDropped = false;   // reported for the interval in which it happened
            count = _count;
            matches = _matches;
            sum = _sum;
            min = _min;
            max = _max;
            last = _last;
            samples = _samples.Count == 0 ? [] : _samples.ToArray();

            _count = 0;
            _matches = 0;
            _sum = 0;
            _min = double.PositiveInfinity;
            _max = double.NegativeInfinity;
            _samples.Clear();
        }

        // Every interval, so series that stopped reporting are forgotten whatever the statistic.
        var seriesTotal = SeriesTotal();

        // The level of an up-down counter, or the latest observation, is known even in an interval without changes: a
        // steady level (active connections that do not change) is still reported.
        var levelKnown = Statistic == MetricStatistic.Last && kind != InstrumentKind.Other;
        if (count < MinimumCount && !levelKnown)
        {
            return null;
        }

        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        var value = Statistic switch
        {
            MetricStatistic.Mean => sum / count,
            MetricStatistic.Min => min,
            MetricStatistic.Max => max,
            MetricStatistic.Sum => sum,
            MetricStatistic.Count => count,
            MetricStatistic.Rate => sum / seconds,
            MetricStatistic.CountRate => count / seconds,
            MetricStatistic.P50 => Percentile(samples, 0.50),
            MetricStatistic.P90 => Percentile(samples, 0.90),
            MetricStatistic.P95 => Percentile(samples, 0.95),
            MetricStatistic.P99 => Percentile(samples, 0.99),
            MetricStatistic.Last => kind switch
            {
                InstrumentKind.UpDownCounter => level,
                InstrumentKind.ObservableCounter or InstrumentKind.ObservableUpDownCounter => seriesTotal,
                _ => last,
            },
            MetricStatistic.Ratio => (double)matches / count,
            _ => sum / count,
        } * Scale;

        var severity = Thresholds.Severity(value, DegradedAbove, UnhealthyAbove, DegradedBelow, UnhealthyBelow);

        var text = value.ToString("G4", CultureInfo.InvariantCulture);
        var capped = seriesDropped ? $" More than {MaxSeries} tag combinations: further ones are not counted." : string.Empty;
        return new Signal(Source, severity, now)
        {
            Value = value,
            Message = $"{Statistic} of {Instrument}: {text} ({count} measurements).{capped}",
            Tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["origin"] = "metric",
                ["instrument"] = Instrument,
                ["statistic"] = Statistic.ToString(),
                ["count"] = count.ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    /// <summary>Whether a series was ignored because <see cref="MaxSeries"/> were already tracked.</summary>
    internal bool SeriesDropped { get; private set; }

    // Caller holds the lock. Returns the series' previous value (this one for a new series).
    private double Observe(string? series, double value)
    {
        var key = series ?? string.Empty;
        if (!_series.TryGetValue(key, out var entry))
        {
            if (_series.Count >= MaxSeries)
            {
                SeriesDropped = true;
                return value;   // not counted: as a series seen for the first time, it adds nothing
            }

            entry = new Series { Total = value };
            _series[key] = entry;
        }

        var previous = entry.Total;
        entry.Total = value;
        entry.Seen = true;
        entry.Missed = 0;
        return previous;
    }

    /// <summary>
    /// Latest value summed over the series observed in this interval: a series that stopped reporting no longer counts.
    /// Series missing for several collections are forgotten.
    /// </summary>
    private double SeriesTotal()
    {
        lock (_gate)
        {
            var total = 0.0;
            List<string>? stale = null;
            foreach (var (key, entry) in _series)
            {
                if (entry.Seen)
                {
                    total += entry.Total;
                    entry.Seen = false;
                }
                else if (++entry.Missed >= MaxMissedCollections)
                {
                    (stale ??= []).Add(key);
                }
            }

            foreach (var key in stale ?? [])
            {
                _series.Remove(key);
            }

            return total;
        }
    }

    private sealed class Series
    {
        public double Total { get; set; }

        public bool Seen { get; set; }

        public int Missed { get; set; }
    }

    private static double Percentile(double[] samples, double quantile)
    {
        Array.Sort(samples);
        var index = (int)Math.Ceiling(quantile * samples.Length) - 1;
        return samples[Math.Clamp(index, 0, samples.Length - 1)];
    }
}
