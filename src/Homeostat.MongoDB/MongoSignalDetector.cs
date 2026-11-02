using System.Collections.Concurrent;
using Homeostat.Detectors;
using Homeostat.Signals;

namespace Homeostat.MongoDB;

/// <summary>
/// Publishes the event-based MongoDB signals every <see cref="MongoSignalOptions.Interval"/>, so conditions see the
/// current state; checkout timeouts are counted over that interval.
/// </summary>
internal sealed class MongoSignalDetector(MongoClientEvents events, IEnumerable<MongoRegistration> registrations, TimeProvider timeProvider) : ISignalDetector
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _published = new(StringComparer.Ordinal);

    public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var registration in registrations)
        {
            // Nothing before the client has heard from a server: no client built yet, or still connecting.
            if (!events.TryGet(registration.Name, out var state) || !state.Subscribed || state.Writable is null)
            {
                continue;
            }

            if (_published.TryGetValue(registration.Name, out var last) && now - last < registration.Options.Interval)
            {
                continue;
            }

            _published[registration.Name] = now;
            MongoSignals.PublishPrimary(signals, registration.Name, state, now);

            var timeouts = state.TakeTimeouts();
            signals.TryPublish(new Signal(MongoSignals.Source(registration.Name, "pool"),
                timeouts > registration.Options.PoolTimeoutsUnhealthyAbove ? SignalSeverity.Unhealthy : SignalSeverity.Healthy, now)
            {
                Value = timeouts,
                Message = timeouts > 0 ? $"{timeouts} connection checkouts timed out: the pool is exhausted." : null,
                Tags = new Dictionary<string, string> { ["origin"] = "mongodb" },
            });
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed record MongoRegistration(string Name, MongoSignalOptions Options);

internal static class MongoSignals
{
    public static string Source(string name, string signal) => $"mongodb:{name}:{signal}";

    public static void PublishPrimary(ISignalPublisher signals, string name, MongoClientEvents.State state, DateTimeOffset at)
    {
        if (state.Writable is not { } writable)
        {
            return;
        }

        signals.TryPublish(new Signal(Source(name, "primary"), writable ? SignalSeverity.Healthy : SignalSeverity.Unhealthy, at)
        {
            Value = state.ConnectedServers,
            Message = writable ? null : "No writable server (primary) is connected: writes fail until one is elected or reachable.",
            Tags = new Dictionary<string, string> { ["origin"] = "mongodb" },
        });
    }
}
