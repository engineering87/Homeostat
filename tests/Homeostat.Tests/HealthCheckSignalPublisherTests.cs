using Homeostat.HealthChecks;
using Homeostat.Signals;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homeostat.Tests;

public class HealthCheckSignalPublisherTests
{
    [Theory]
    [InlineData(HealthStatus.Healthy, SignalSeverity.Healthy)]
    [InlineData(HealthStatus.Degraded, SignalSeverity.Degraded)]
    [InlineData(HealthStatus.Unhealthy, SignalSeverity.Unhealthy)]
    public void Maps_health_status_to_severity(HealthStatus status, SignalSeverity expected)
        => Assert.Equal(expected, HealthCheckSignalPublisher.Map(status));

    [Fact]
    public async Task Publishes_one_signal_per_entry_using_the_entry_name_as_source()
    {
        var sink = new CollectingPublisher();
        var publisher = new HealthCheckSignalPublisher(sink, TimeProvider.System);
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["weather-feed"] = new(HealthStatus.Unhealthy, "down", TimeSpan.FromMilliseconds(12), null, null),
                ["air-quality"] = new(HealthStatus.Healthy, null, TimeSpan.FromMilliseconds(3), null, null),
            },
            TimeSpan.FromMilliseconds(15));

        await publisher.PublishAsync(report, CancellationToken.None);

        Assert.Collection(
            sink.Signals.OrderBy(s => s.Source, StringComparer.Ordinal),
            s => { Assert.Equal("air-quality", s.Source); Assert.Equal(SignalSeverity.Healthy, s.Severity); },
            s => { Assert.Equal("weather-feed", s.Source); Assert.Equal(SignalSeverity.Unhealthy, s.Severity); Assert.Equal("down", s.Message); });
    }

    [Fact]
    public async Task Skips_the_homeostat_check_itself()
    {
        var sink = new CollectingPublisher();
        var publisher = new HealthCheckSignalPublisher(sink, TimeProvider.System);
        var report = new HealthReport(
            new Dictionary<string, HealthReportEntry>
            {
                ["homeostat"] = new(HealthStatus.Degraded, "paused", TimeSpan.Zero, null, null, new[] { "homeostat:self" }),
                ["weather-feed"] = new(HealthStatus.Healthy, null, TimeSpan.Zero, null, null),
            },
            TimeSpan.Zero);

        await publisher.PublishAsync(report, CancellationToken.None);

        Assert.Equal("weather-feed", Assert.Single(sink.Signals).Source);
    }
}
