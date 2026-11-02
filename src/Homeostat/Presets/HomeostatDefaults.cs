namespace Homeostat.Presets;

/// <summary>
/// The conditions of a starter preset. Each one watches a signal that the preset also registers, and only observes
/// until you give it a plan. Turn off the ones you do not want with <see cref="DefaultCondition.Enabled"/>.
/// </summary>
public sealed class HomeostatDefaults
{
    internal HomeostatDefaults(bool webApi)
    {
        ThreadPoolStarvation = new("threadpool-starved", "threadpool", TimeSpan.FromSeconds(30), enabled: true);
        MemoryPressure = new("memory-pressure", "memory", TimeSpan.FromMinutes(2), enabled: true);
        ExceptionRate = new("exception-rate-high", "exceptions", TimeSpan.FromMinutes(1), enabled: true);
        ServerErrors = new("server-errors-high", "http-server:errors", TimeSpan.FromMinutes(1), enabled: webApi);
        ServerLatency = new("server-latency-high", "http-server:latency", TimeSpan.FromMinutes(2), enabled: webApi);
    }

    /// <summary>
    /// <c>threadpool-starved</c>: the thread pool cannot keep up (source <c>threadpool</c>), for 30 seconds. Usually
    /// the cause of the other symptoms, so the server conditions are suppressed while it is active.
    /// </summary>
    public DefaultCondition ThreadPoolStarvation { get; }

    /// <summary><c>memory-pressure</c>: memory load is high (source <c>memory</c>), for 2 minutes. Suppresses the latency condition.</summary>
    public DefaultCondition MemoryPressure { get; }

    /// <summary><c>exception-rate-high</c>: exceptions are thrown at an unusual rate (source <c>exceptions</c>), for 1 minute.</summary>
    public DefaultCondition ExceptionRate { get; }

    /// <summary>
    /// <c>server-errors-high</c>: at least 5% of the HTTP requests answered 5xx (source <c>http-server:errors</c>), for
    /// 1 minute. On by default in <c>AddWebApiDefaults</c> only; needs ASP.NET Core.
    /// </summary>
    public DefaultCondition ServerErrors { get; }

    /// <summary>
    /// <c>server-latency-high</c>: the 95th percentile of HTTP request durations is at least 2 seconds (source
    /// <c>http-server:latency</c>), for 2 minutes. On by default in <c>AddWebApiDefaults</c> only; needs ASP.NET Core.
    /// </summary>
    public DefaultCondition ServerLatency { get; }
}
