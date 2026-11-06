namespace Homeostat.Metrics;

/// <summary>Settings shared by the ASP.NET Core server signals.</summary>
public abstract class ServerSignalOptions
{
    private protected ServerSignalOptions(string source) => Source = source;

    /// <summary>Signal source.</summary>
    public string Source { get; set; }

    /// <summary>Intervals with fewer requests publish no signal. Defaults to 20.</summary>
    public int MinimumRequests { get; set; } = 20;
}

/// <summary>Settings of <c>AddServerErrorRateSignal</c>: source <c>http-server:errors</c>, value the share of 5xx responses.</summary>
public sealed class ServerErrorRateSignalOptions() : ServerSignalOptions("http-server:errors")
{
    /// <summary>Share of 5xx responses (0 to 1) from which the signal is Degraded. Defaults to 0.01.</summary>
    public double DegradedErrorRatio { get; set; } = 0.01;

    /// <summary>Share of 5xx responses (0 to 1) from which the signal is Unhealthy. Defaults to 0.05.</summary>
    public double UnhealthyErrorRatio { get; set; } = 0.05;
}

/// <summary>Settings of <c>AddServerLatencySignal</c>: source <c>http-server:latency</c>, value the p95 in seconds.</summary>
public sealed class ServerLatencySignalOptions() : ServerSignalOptions("http-server:latency")
{
    /// <summary>95th percentile from which the signal is Degraded. Defaults to 500 milliseconds.</summary>
    public TimeSpan DegradedLatency { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>95th percentile from which the signal is Unhealthy. Defaults to 2 seconds.</summary>
    public TimeSpan UnhealthyLatency { get; set; } = TimeSpan.FromSeconds(2);
}
