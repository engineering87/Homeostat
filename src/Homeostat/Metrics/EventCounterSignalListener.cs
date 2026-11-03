using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;
using Homeostat.Signals;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homeostat.Metrics;

/// <summary>Listens to EventCounters while the host runs and publishes the registered ones as signals.</summary>
internal sealed class EventCounterSignalHost(
    IEnumerable<EventCounterRegistration> registrations, ISignalPublisher publisher, TimeProvider timeProvider, ILogger<EventCounterSignalHost> logger)
    : IHostedService, IDisposable
{
    private EventCounterSignalListener? _listener;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var list = registrations.ToList();
        if (list.Count > 0)
        {
            _listener = new EventCounterSignalListener(list, publisher, timeProvider, logger);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }
}

/// <summary>
/// An <see cref="EventListener"/> that enables only the counters of the registered event sources: it enables them at
/// the <see cref="EventLevel.Critical"/> level, so their ordinary (tracing) events stay off, while counter reports,
/// written at <see cref="EventLevel.LogAlways"/>, arrive.
/// </summary>
internal sealed class EventCounterSignalListener : EventListener
{
    // Field initializers run before the base constructor, which reports the existing event sources.
    private readonly ConcurrentQueue<EventSource> _early = new();
    private readonly Dictionary<string, List<EventCounterRegistration>> _bySource;
    private readonly ISignalPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    // Not readonly: the base constructor calls OnEventSourceCreated before this constructor runs, and those calls must
    // see false. Volatile, because event callbacks run on other threads.
    private volatile bool _ready;

    public EventCounterSignalListener(
        IReadOnlyList<EventCounterRegistration> registrations, ISignalPublisher publisher, TimeProvider timeProvider, ILogger logger)
    {
        _bySource = registrations.GroupBy(r => r.EventSource, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        _publisher = publisher;
        _timeProvider = timeProvider;
        _logger = logger;
        lock (_early)
        {
            _ready = true;
        }

        while (_early.TryDequeue(out var source))
        {
            Enable(source);
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        // Sources created while the constructor runs (on other threads too) are queued under the lock and enabled by
        // the constructor, which sets _ready under the same lock.
        lock (_early)
        {
            if (!_ready)
            {
                _early.Enqueue(eventSource);
                return;
            }
        }

        Enable(eventSource);
    }

    private void Enable(EventSource source)
    {
        if (!_bySource.TryGetValue(source.Name, out var registrations))
        {
            return;
        }

        var interval = registrations.Min(r => r.Options.IntervalSeconds);
        EnableEvents(source, EventLevel.Critical, EventKeywords.None, new Dictionary<string, string?>
        {
            ["EventCounterIntervalSec"] = interval.ToString(CultureInfo.InvariantCulture),
        });
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!_ready || eventData.EventName != "EventCounters" || eventData.Payload is not { Count: > 0 } payload
            || payload[0] is not IDictionary<string, object?> counter
            || !counter.TryGetValue("Name", out var nameValue) || nameValue is not string name
            || !_bySource.TryGetValue(eventData.EventSource.Name, out var registrations))
        {
            return;
        }

        // Polling counters report a mean; incrementing counters an increment over the interval, which another session
        // (dotnet-counters, a telemetry agent) may have shortened: turned into a rate per second, so thresholds hold.
        double raw;
        if (counter.TryGetValue("Mean", out var mean) && mean is double m)
        {
            raw = m;
        }
        else if (counter.TryGetValue("Increment", out var increment) && increment is double i
            && counter.TryGetValue("IntervalSec", out var seconds) && Convert.ToDouble(seconds, CultureInfo.InvariantCulture) is > 0 and var interval)
        {
            raw = i / interval;
        }
        else
        {
            return;
        }

        if (!double.IsFinite(raw))
        {
            return;
        }

        foreach (var registration in registrations)
        {
            if (!string.Equals(registration.Counter, name, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var value = registration.Options.Transform is { } transform ? transform(raw) : raw;
                if (!double.IsFinite(value))
                {
                    continue;
                }

                _publisher.TryPublish(new Signal(registration.Source, registration.Options.SeverityOf(value), _timeProvider.GetUtcNow())
                {
                    Value = value,
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Turning the counter {Counter} of {EventSource} into a signal failed.", name, eventData.EventSource.Name);
            }
        }
    }
}
