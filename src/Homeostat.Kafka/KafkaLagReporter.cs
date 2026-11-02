using System.Text.Json;
using Homeostat.Signals;

namespace Homeostat.Kafka;

/// <summary>
/// Turns the statistics that the Kafka client emits (<c>statistics.interval.ms</c>) into a consumer lag signal: the
/// messages behind the high watermark, summed over the partitions assigned to the consumer, with the partition count
/// and the largest partition lag as tags. Nothing is queried: the client already knows the lag.
/// </summary>
public sealed class KafkaLagReporter
{
    private readonly ISignalPublisher _publisher;
    private readonly string _source;
    private readonly KafkaLagOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a reporter.</summary>
    /// <param name="publisher">Where signals go (from the services).</param>
    /// <param name="name">Consumer name, used in the default source <c>kafka:{name}:lag</c>.</param>
    /// <param name="options">Thresholds and source.</param>
    /// <param name="timeProvider">Clock of the signal timestamps; the system clock by default.</param>
    public KafkaLagReporter(ISignalPublisher publisher, string name, KafkaLagOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _publisher = publisher;
        _options = options ?? new KafkaLagOptions();
        _source = _options.Source ?? $"kafka:{name}:lag";
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_options.DegradedAbove > _options.UnhealthyAbove)
        {
            throw new ArgumentException("DegradedAbove cannot be greater than UnhealthyAbove.", nameof(options));
        }
    }

    /// <summary>Signal source.</summary>
    public string Source => _source;

    /// <summary>
    /// Reports one statistics document. Pass it to <c>SetStatisticsHandler</c>, or call it from your own handler.
    /// Documents without assigned partitions (before the first assignment) publish nothing.
    /// </summary>
    /// <param name="statisticsJson">The statistics JSON.</param>
    public void Report(string statisticsJson)
    {
        if (Measure(statisticsJson, _options.FromCommittedOffsets) is not { } lag)
        {
            return;
        }

        var severity = lag.Total > _options.UnhealthyAbove ? SignalSeverity.Unhealthy
            : lag.Total > _options.DegradedAbove ? SignalSeverity.Degraded
            : SignalSeverity.Healthy;
        _publisher.TryPublish(new Signal(_source, severity, _timeProvider.GetUtcNow())
        {
            Value = lag.Total,
            Tags = new Dictionary<string, string>
            {
                ["partitions"] = lag.Partitions.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["max-partition-lag"] = lag.MaxPartition.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["origin"] = "kafka",
            },
        });
    }

    /// <summary>
    /// Total lag over the partitions with a known lag (librdkafka reports -1 when it does not know it yet, and the
    /// partition -1 holds internal counters); <c>null</c> when no partition has a known lag or the document is invalid.
    /// </summary>
    internal static (long Total, int Partitions, long MaxPartition)? Measure(string statisticsJson, bool fromCommitted)
    {
        try
        {
            using var document = JsonDocument.Parse(statisticsJson, new JsonDocumentOptions { MaxDepth = 64 });
            if (!document.RootElement.TryGetProperty("topics", out var topics) || topics.ValueKind != JsonValueKind.Object)
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
                    if (!p.TryGetProperty(field, out var value) && !p.TryGetProperty("consumer_lag", out value))
                    {
                        continue;
                    }

                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var lag) || lag < 0)
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
        catch (JsonException)
        {
            return null;
        }
    }
}
