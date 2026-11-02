namespace Homeostat.Kafka;

/// <summary>Settings of the consumer lag signal.</summary>
public sealed class KafkaLagOptions
{
    /// <summary>Signal source; defaults to <c>kafka:{name}:lag</c>.</summary>
    public string? Source { get; set; }

    /// <summary>Total lag (messages behind, over the assigned partitions) above which the signal is Degraded.</summary>
    public double? DegradedAbove { get; set; }

    /// <summary>Total lag above which the signal is Unhealthy.</summary>
    public double? UnhealthyAbove { get; set; }

    /// <summary>
    /// Measures the lag from the stored offsets (what the application has processed, the default) or from the
    /// committed offsets (what a restarted consumer would start from).
    /// </summary>
    public bool FromCommittedOffsets { get; set; }
}
