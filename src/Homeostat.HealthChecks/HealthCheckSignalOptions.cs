using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homeostat.HealthChecks;

/// <summary>Settings of <c>AddHealthCheckSignals</c>.</summary>
public sealed class HealthCheckSignalOptions
{
    /// <summary>
    /// How often health checks run (at least one second). When <c>null</c> (the default), the framework default applies
    /// (30 seconds). Keep it shorter than the <c>SustainedFor</c> of your conditions. This sets
    /// <see cref="HealthCheckPublisherOptions.Period"/>, which is shared by every <see cref="IHealthCheckPublisher"/> of
    /// the application: other publishers run at the same pace.
    /// </summary>
    public TimeSpan? Period { get; set; }
}
