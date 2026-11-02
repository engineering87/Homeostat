using System.Diagnostics.Metrics;
using Homeostat.Detectors;
using Homeostat.Signals;

namespace Homeostat.Metrics;

/// <summary>
/// Listens to <see cref="System.Diagnostics.Metrics"/> instruments with a <see cref="MeterListener"/> and turns each
/// watched instrument into a signal at every detection interval.
/// </summary>
internal sealed class MetricSignalDetector : ISignalDetector, IDisposable
{
    private readonly MetricWatch[] _watches;
    private readonly TimeProvider _timeProvider;
    private readonly MeterListener _listener = new();
    private long _lastCollect;
    private int _collected;

    public MetricSignalDetector(IEnumerable<MetricWatch> watches, TimeProvider timeProvider)
    {
        _watches = watches.ToArray();
        _timeProvider = timeProvider;
        _lastCollect = timeProvider.GetTimestamp();

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            var matching = _watches.Where(w => w.Matches(instrument.Meter.Name, instrument.Name)).ToArray();
            if (matching.Length > 0)
            {
                listener.EnableMeasurementEvents(instrument, new Subscription(matching, KindOf(instrument)));
            }
        };

        _listener.SetMeasurementEventCallback<double>((_, value, tags, state) => Record(value, tags, state));
        _listener.SetMeasurementEventCallback<float>((_, value, tags, state) => Record(value, tags, state));
        _listener.SetMeasurementEventCallback<long>((_, value, tags, state) => Record(value, tags, state));
        _listener.SetMeasurementEventCallback<int>((_, value, tags, state) => Record(value, tags, state));
        _listener.SetMeasurementEventCallback<short>((_, value, tags, state) => Record(value, tags, state));
        _listener.SetMeasurementEventCallback<byte>((_, value, tags, state) => Record(value, tags, state));
        _listener.SetMeasurementEventCallback<decimal>((_, value, tags, state) => Record((double)value, tags, state));
        _listener.Start();
    }

    public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
    {
        // Observable instruments report only when asked.
        _listener.RecordObservableInstruments();

        var now = _timeProvider.GetUtcNow();
        var timestamp = _timeProvider.GetTimestamp();
        var elapsed = _timeProvider.GetElapsedTime(Interlocked.Exchange(ref _lastCollect, timestamp), timestamp);
        var first = Interlocked.Exchange(ref _collected, 1) == 0;

        foreach (var watch in _watches)
        {
            // The first interval is partial (it starts when the application starts): use it only as a baseline,
            // otherwise rates computed over a few milliseconds would raise false alarms.
            if (watch.Collect(now, elapsed) is { } signal && !first)
            {
                signals.TryPublish(signal);
            }
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose() => _listener.Dispose();

    private static InstrumentKind KindOf(Instrument instrument)
    {
        if (!instrument.GetType().IsGenericType)
        {
            return InstrumentKind.Other;
        }

        var definition = instrument.GetType().GetGenericTypeDefinition();
        return definition == typeof(UpDownCounter<>) ? InstrumentKind.UpDownCounter
            : definition == typeof(ObservableCounter<>) ? InstrumentKind.ObservableCounter
            : definition == typeof(ObservableUpDownCounter<>) ? InstrumentKind.ObservableUpDownCounter
            : InstrumentKind.Other;
    }

    private static void Record(double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (state is not Subscription subscription)
        {
            return;
        }

        // Runs inside the code that records the metric (for example ASP.NET Core's request pipeline): never throw.
        try
        {
            Dictionary<string, object?>? dictionary = null;
            string? series = null;
            foreach (var watch in subscription.Watches)
            {
                var needsTags = watch.NeedsTags || subscription.Kind is InstrumentKind.ObservableCounter or InstrumentKind.ObservableUpDownCounter;
                if (needsTags && dictionary is null)
                {
                    dictionary = new Dictionary<string, object?>(tags.Length, StringComparer.Ordinal);
                    foreach (var tag in tags)
                    {
                        dictionary[tag.Key] = tag.Value;
                    }
                }

                if (subscription.Kind is InstrumentKind.ObservableCounter or InstrumentKind.ObservableUpDownCounter)
                {
                    series ??= string.Join('|', dictionary!.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={MetricSignalBuilder.Text(t.Value)}"));
                }

                try
                {
                    watch.Record(value, needsTags ? dictionary : null, subscription.Kind, series);
                }
                catch (Exception)
                {
                    // A filter that throws (for example a cast on a tag of another type) only skips this measurement.
                }
            }
        }
        catch (Exception)
        {
            // Never fail the instrumented code.
        }
    }

    private sealed record Subscription(MetricWatch[] Watches, InstrumentKind Kind);
}
