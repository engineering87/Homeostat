namespace Homeostat.Kafka;

/// <summary>Settings of the signals read from the Kafka client statistics.</summary>
public sealed class KafkaSignalOptions
{
    /// <summary>Source prefix of the signals: <c>{prefix}:lag</c>, <c>{prefix}:brokers</c>, <c>{prefix}:queue</c>. Defaults to <c>kafka:{name}</c>.</summary>
    public string? Source { get; set; }

    /// <summary>Thresholds of the consumer lag (messages behind, over the partitions) of <c>{prefix}:lag</c>; none by default.</summary>
    public KafkaThresholds Lag { get; } = new();

    /// <summary>
    /// Measures the lag from the stored offsets (the default) or from the committed offsets (what a restarted consumer
    /// would start from). By default the client stores an offset when <c>Consume</c> returns the message, before it is
    /// handled; for a lag of what was actually handled, set <c>EnableAutoOffsetStore = false</c> and call
    /// <c>StoreOffset</c> after handling each message.
    /// </summary>
    public bool FromCommittedOffsets { get; set; }

    /// <summary>
    /// Thresholds of the share of the producer queue in use (<c>queue.buffering.max.messages</c>) of
    /// <c>{prefix}:queue</c>; Degraded above 0.8 and Unhealthy above 0.95 by default. When the queue is full,
    /// <c>Produce</c> fails with "Local: Queue full".
    /// </summary>
    public KafkaThresholds Queue { get; } = new() { DegradedAbove = 0.8, UnhealthyAbove = 0.95 };

    internal void Validate()
    {
        if (Lag.DegradedAbove > Lag.UnhealthyAbove || Queue.DegradedAbove > Queue.UnhealthyAbove)
        {
            throw new ArgumentException("A Degraded threshold cannot be greater than the Unhealthy one.");
        }
    }
}

/// <summary>Upper thresholds of one Kafka signal.</summary>
public sealed class KafkaThresholds
{
    /// <summary>Value above which the signal is Degraded; <c>null</c> for none.</summary>
    public double? DegradedAbove { get; set; }

    /// <summary>Value above which the signal is Unhealthy; <c>null</c> for none.</summary>
    public double? UnhealthyAbove { get; set; }
}
