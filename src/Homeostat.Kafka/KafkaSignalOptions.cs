namespace Homeostat.Kafka;

/// <summary>Settings of the signals read from the Kafka client statistics.</summary>
public sealed class KafkaSignalOptions
{
    /// <summary>Source prefix of the signals: <c>{prefix}:lag</c>, <c>{prefix}:brokers</c>, <c>{prefix}:queue</c>. Defaults to <c>kafka:{name}</c>.</summary>
    public string? Source { get; set; }

    /// <summary>Consumer lag (messages behind, over the partitions) above which <c>{prefix}:lag</c> is Degraded.</summary>
    public double? LagDegradedAbove { get; set; }

    /// <summary>Consumer lag above which <c>{prefix}:lag</c> is Unhealthy.</summary>
    public double? LagUnhealthyAbove { get; set; }

    /// <summary>
    /// Measures the lag from the stored offsets (the default) or from the committed offsets (what a restarted consumer
    /// would start from). By default the client stores an offset when <c>Consume</c> returns the message, before it is
    /// handled; for a lag of what was actually handled, set <c>EnableAutoOffsetStore = false</c> and call
    /// <c>StoreOffset</c> after handling each message.
    /// </summary>
    public bool FromCommittedOffsets { get; set; }

    /// <summary>
    /// Share of the producer queue in use (<c>queue.buffering.max.messages</c>) above which <c>{prefix}:queue</c> is
    /// Degraded; defaults to 0.8. When the queue is full, <c>Produce</c> fails with "Local: Queue full".
    /// </summary>
    public double QueueDegradedAbove { get; set; } = 0.8;

    /// <summary>Share of the producer queue in use above which <c>{prefix}:queue</c> is Unhealthy; defaults to 0.95.</summary>
    public double QueueUnhealthyAbove { get; set; } = 0.95;

    internal void Validate()
    {
        if (LagDegradedAbove > LagUnhealthyAbove || QueueDegradedAbove > QueueUnhealthyAbove)
        {
            throw new ArgumentException("A Degraded threshold cannot be greater than the Unhealthy one.");
        }
    }
}
