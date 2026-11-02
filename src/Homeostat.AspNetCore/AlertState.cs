using System.Globalization;
using Homeostat.Signals;

namespace Homeostat.AspNetCore;

/// <summary>
/// The alerts received by one set of alert endpoints, per source. Alert senders notify each change once (and
/// repeat a firing alert only every few hours), while conditions need a steady stream of signals for
/// <c>SustainedFor</c> and <c>ClearAfter</c>, and one alert name can cover several series (one per instance).
/// So the state is kept here and the aggregated signal of every source is published when an alert arrives and again
/// at every detection interval: a source is as bad as its worst firing series, and Healthy only when none fires.
/// </summary>
internal sealed class AlertState : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SourceState> _sources = new(StringComparer.Ordinal);
    private readonly AlertIngestionOptions _options;
    private readonly ISignalPublisher _signals;
    private readonly TimeProvider _timeProvider;
    private ITimer? _timer;

    public AlertState(AlertIngestionOptions options, ISignalPublisher signals, TimeProvider timeProvider)
    {
        _options = options;
        _signals = signals;
        _timeProvider = timeProvider;
    }

    /// <summary>Starts republishing the state at <paramref name="interval"/>.</summary>
    public void Start(TimeSpan interval)
        => _timer = _timeProvider.CreateTimer(static state => ((AlertState)state!).PublishAll(), this, interval, interval);

    /// <summary>Applies the observations and publishes the affected sources; returns how many were accepted.</summary>
    public int Apply(IReadOnlyList<AlertObservation> observations)
    {
        var now = _timeProvider.GetUtcNow();
        var accepted = 0;
        var affected = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var observation in observations)
            {
                if (!_sources.TryGetValue(observation.Source, out var source))
                {
                    if (_sources.Count >= _options.MaxTrackedSources)
                    {
                        continue;
                    }

                    source = new SourceState();
                    _sources[observation.Source] = source;
                }

                if (observation.Firing)
                {
                    source.Firing[observation.Key] = (observation, now);
                }
                else
                {
                    source.Firing.Remove(observation.Key);
                    source.LastResolved = observation;
                }

                source.LastChange = now;
                affected.Add(observation.Source);
                accepted++;
            }
        }

        foreach (var name in affected)
        {
            Publish(name, now);
        }

        return accepted;
    }

    public void Dispose() => _timer?.Dispose();

    private void PublishAll()
    {
        var now = _timeProvider.GetUtcNow();
        string[] names;
        lock (_gate)
        {
            names = [.. _sources.Keys];
        }

        foreach (var name in names)
        {
            Publish(name, now);
        }
    }

    private void Publish(string name, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(name, out var source))
            {
                return;
            }

            // A firing series not confirmed within the time to live is dropped: some senders never resolve.
            foreach (var key in source.Firing.Where(f => now - f.Value.ReceivedAt > _options.FiringTimeToLive).Select(f => f.Key).ToList())
            {
                source.Firing.Remove(key);
                source.LastChange = now;
            }

            if (source.Firing.Count == 0 && now - source.LastChange > _options.FiringTimeToLive)
            {
                _sources.Remove(name); // Healthy for long enough: stop republishing it.
                return;
            }

            // Published under the lock, so a periodic publication never overtakes a newer one (TryPublish does not block).
            _signals.TryPublish(Aggregate(name, source));
        }
    }

    private Signal Aggregate(string name, SourceState source)
    {
        var now = _timeProvider.GetUtcNow();
        if (source.Firing.Count == 0)
        {
            var tags = source.LastResolved is { } resolved
                ? new Dictionary<string, string>(resolved.Tags, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            tags["status"] = "resolved";
            tags["firing"] = "0";
            return new Signal(name, SignalSeverity.Healthy, now)
            {
                Message = source.LastResolved?.Message ?? "No firing alerts.",
                Tags = tags,
            };
        }

        // The worst series decides; among equals, the most recent one describes the signal.
        var worst = source.Firing.Values
            .OrderByDescending(f => f.Observation.Severity)
            .ThenByDescending(f => f.ReceivedAt)
            .First().Observation;
        var firingTags = new Dictionary<string, string>(worst.Tags, StringComparer.Ordinal)
        {
            ["status"] = "firing",
            ["firing"] = source.Firing.Count.ToString(CultureInfo.InvariantCulture),
        };
        return new Signal(name, worst.Severity, now)
        {
            Value = worst.Value,
            Message = source.Firing.Count == 1 ? worst.Message : $"{worst.Message} ({source.Firing.Count} firing series)",
            Tags = firingTags,
        };
    }

    private sealed class SourceState
    {
        public Dictionary<string, (AlertObservation Observation, DateTimeOffset ReceivedAt)> Firing { get; } = new(StringComparer.Ordinal);

        public AlertObservation? LastResolved { get; set; }

        public DateTimeOffset LastChange { get; set; }
    }
}
