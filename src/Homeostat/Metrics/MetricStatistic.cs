namespace Homeostat.Metrics;

/// <summary>Value computed from the measurements of one detection interval.</summary>
public enum MetricStatistic
{
    /// <summary>Average of the measurements.</summary>
    Mean,

    /// <summary>Smallest measurement.</summary>
    Min,

    /// <summary>Largest measurement.</summary>
    Max,

    /// <summary>Sum of the measurements.</summary>
    Sum,

    /// <summary>Number of measurements.</summary>
    Count,

    /// <summary>Sum per second, for example increments of a counter per second.</summary>
    Rate,

    /// <summary>Measurements per second, for example requests per second from a duration histogram.</summary>
    CountRate,

    /// <summary>Median (50th percentile).</summary>
    P50,

    /// <summary>90th percentile.</summary>
    P90,

    /// <summary>95th percentile.</summary>
    P95,

    /// <summary>99th percentile.</summary>
    P99,

    /// <summary>Most recent measurement, for gauges.</summary>
    Last,

    /// <summary>Fraction (0 to 1) of the measurements that match <see cref="MetricSignalBuilder.RatioOf"/>.</summary>
    Ratio,
}
