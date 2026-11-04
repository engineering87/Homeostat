using System.Globalization;
using System.Text.Json;
using Homeostat.Signals;

namespace Homeostat.Kafka;

/// <summary>
/// Turns the statistics that the Kafka client emits (<c>statistics.interval.ms</c>) into signals, without any query:
/// <list type="bullet">
/// <item><description><c>kafka:{name}:brokers</c>: the brokers the client is connected to (Unhealthy when none is up,
/// Degraded when some are down), for producers and consumers;</description></item>
/// <item><description><c>kafka:{name}:lag</c>: for consumers, the messages behind the high watermark, summed over the
/// partitions, with the partition count and the largest partition lag as tags;</description></item>
/// <item><description><c>kafka:{name}:queue</c>: for producers, the share of the local queue in use.</description></item>
/// </list>
/// </summary>
public sealed class KafkaSignalReporter
{
    private readonly ISignalPublisher _publisher;
    private readonly string _source;
    private readonly KafkaSignalOptions _options;
    private readonly TimeProvider _timeProvider;
    private int _lastBrokerSeverity;

    /// <summary>Creates a reporter that turns Kafka client statistics, passed to <see cref="Report"/>, into signals. Use it when the client already has a statistics handler of yours.</summary>
    /// <param name="publisher">Where signals go (from the services).</param>
    /// <param name="name">Client name, used in the default source <c>kafka:{name}</c>.</param>
    /// <param name="options">Thresholds and source.</param>
    /// <param name="timeProvider">Clock of the signal timestamps; the system clock by default.</param>
    public KafkaSignalReporter(ISignalPublisher publisher, string name, KafkaSignalOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _publisher = publisher;
        _options = options ?? new KafkaSignalOptions();
        _options.Validate();
        _source = _options.Source ?? $"kafka:{name}";
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Source prefix of the signals.</summary>
    public string Source => _source;

    /// <summary>
    /// Reports one statistics document. Pass it to <c>SetStatisticsHandler</c>, or call it from your own handler.
    /// Invalid documents are ignored.
    /// </summary>
    /// <param name="statisticsJson">The statistics JSON.</param>
    public void Report(string statisticsJson)
    {
        // The client rethrows a handler exception from the next Consume: never let one escape.
        try
        {
            ReportCore(statisticsJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // An unexpected document: skip it.
        }
    }

    private void ReportCore(string statisticsJson)
    {
        var statistics = Read(statisticsJson, _options.FromCommittedOffsets);
        var now = _timeProvider.GetUtcNow();
        if (statistics.Brokers is { } brokers)
        {
            var severity = brokers.Up == 0 ? SignalSeverity.Unhealthy : brokers.Up < brokers.Used ? SignalSeverity.Degraded : SignalSeverity.Healthy;

            // A connection can be between a close and a reconnect at the moment of the statistics: a broker problem is
            // reported only when two documents in a row show it. The first one publishes nothing, rather than a
            // better state than it saw (a Healthy signal would reset SustainedFor or satisfy ClearAfter).
            var previous = (SignalSeverity)Interlocked.Exchange(ref _lastBrokerSeverity, (int)severity);
            if (severity <= previous)
            {
                Publish($"{_source}:brokers", severity, brokers.Up, now, new() { ["used"] = Text(brokers.Used) });
            }
        }

        if (statistics.Lag is { } lag)
        {
            var severity = lag.Total > _options.Lag.UnhealthyAbove ? SignalSeverity.Unhealthy
                : lag.Total > _options.Lag.DegradedAbove ? SignalSeverity.Degraded
                : SignalSeverity.Healthy;
            Publish($"{_source}:lag", severity, lag.Total, now, new()
            {
                ["partitions"] = Text(lag.Partitions),
                ["max-partition-lag"] = Text(lag.MaxPartition),
            });
        }

        if (statistics.QueueShare is { } share)
        {
            var severity = share > _options.Queue.UnhealthyAbove ? SignalSeverity.Unhealthy
                : share > _options.Queue.DegradedAbove ? SignalSeverity.Degraded
                : SignalSeverity.Healthy;
            Publish($"{_source}:queue", severity, share, now, []);
        }
    }

    private void Publish(string source, SignalSeverity severity, double value, DateTimeOffset at, Dictionary<string, string> tags)
    {
        tags["origin"] = "kafka";
        _publisher.TryPublish(new Signal(source, severity, at) { Value = value, Tags = tags });
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal sealed record Statistics((int Used, int Up)? Brokers, (long Total, int Partitions, long MaxPartition)? Lag, double? QueueShare);

    /// <summary>Reads the parts of the librdkafka statistics that the signals need.</summary>
    internal static Statistics Read(string statisticsJson, bool fromCommitted)
    {
        using var document = JsonDocument.Parse(statisticsJson, new JsonDocumentOptions { MaxDepth = 64 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Not a statistics document.");
        }

        var type = String(root, "type");
        return new Statistics(
            Brokers(root),
            type == "producer" ? null : Lag(root, fromCommitted),
            type == "consumer" ? null : QueueShare(root));
    }

    /// <summary>
    /// The brokers the client uses: those that lead partitions it produces to or fetches from (non-empty
    /// <c>toppars</c>). The client connects to the others only when needed, so their idle or closed connections say
    /// nothing. Until it uses any (at startup, or with nothing assigned), it counts whether any broker, bootstrap
    /// included, is up: <c>null</c> (nothing to publish) when none is known at all.
    /// </summary>
    private static (int Used, int Up)? Brokers(JsonElement root)
    {
        if (!root.TryGetProperty("brokers", out var brokers) || brokers.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int used = 0, usedUp = 0, any = 0, anyUp = 0;
        foreach (var broker in brokers.EnumerateObject())
        {
            var b = broker.Value;
            if (b.ValueKind != JsonValueKind.Object || String(b, "source") is "logical" or "internal")
            {
                continue;
            }

            var up = String(b, "state") == "UP";
            any++;
            anyUp += up ? 1 : 0;
            if (b.TryGetProperty("toppars", out var toppars) && toppars.ValueKind == JsonValueKind.Object && toppars.EnumerateObject().Any())
            {
                used++;
                usedUp += up ? 1 : 0;
            }
        }

        return used > 0 ? (used, usedUp) : any > 0 ? (1, anyUp > 0 ? 1 : 0) : null;
    }

    private static string? String(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Total lag over the partitions with a known lag (librdkafka reports -1 when it does not know it yet, and the
    /// partition -1 holds internal counters); <c>null</c> when no partition has a known lag.
    /// </summary>
    private static (long Total, int Partitions, long MaxPartition)? Lag(JsonElement root, bool fromCommitted)
    {
        if (!root.TryGetProperty("topics", out var topics) || topics.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        long total = 0, max = 0;
        var partitions = 0;
        foreach (var topic in topics.EnumerateObject())
        {
            if (!topic.Value.TryGetProperty("partitions", out var parts) || parts.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var part in parts.EnumerateObject())
            {
                var p = part.Value;
                if (part.Name == "-1" || p.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var field = fromCommitted ? "consumer_lag" : "consumer_lag_stored";
                if ((!p.TryGetProperty(field, out var value) && !p.TryGetProperty("consumer_lag", out value))
                    || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var lag) || lag < 0)
                {
                    continue;
                }

                total += lag;
                max = Math.Max(max, lag);
                partitions++;
            }
        }

        return partitions == 0 ? null : (total, partitions, max);
    }

    /// <summary>
    /// The fuller of the two producer queue limits: messages (<c>queue.buffering.max.messages</c>) and bytes
    /// (<c>queue.buffering.max.kbytes</c>). Either one full makes <c>Produce</c> fail.
    /// </summary>
    private static double? QueueShare(JsonElement root)
    {
        double? Share(string count, string max)
            => root.TryGetProperty(count, out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var used)
                && root.TryGetProperty(max, out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt64(out var limit) && limit > 0
                ? (double)used / limit
                : null;

        var messages = Share("msg_cnt", "msg_max");
        var bytes = Share("msg_size", "msg_size_max");
        return messages is null && bytes is null ? null : Math.Max(messages ?? 0, bytes ?? 0);
    }
}
