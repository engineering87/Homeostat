using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Samples.Architectures.TelemetryIngestion;

/// <summary>
/// What the team knows about the telemetry ingestion service, written down for Homeostat. The reasoning behind each
/// line is in docs/architecture-examples.md.
/// </summary>
public static class TelemetryIngestionHealing
{
    /// <summary>Name of the pause gate the ingestion loop waits on.</summary>
    public const string Consumer = "telemetry-ingest";

    /// <summary>Drop the diagnostic readings (debug channel) and keep the measurements.</summary>
    public const string SkipDiagnosticReadings = "skip-diagnostic-readings";

    /// <summary>Write fewer, larger batches.</summary>
    public const string LargerWriteBatches = "larger-write-batches";

    /// <summary>Adds the probes, the pause gate, the conditions and the plans of the ingestion service.</summary>
    public static HomeostatBuilder AddTelemetryIngestionHealing(this HomeostatBuilder h) => h
        .AddPauseGate(Consumer)
        .AddLatencyProbe("timeseries-store",
            (sp, ct) => sp.GetRequiredService<ITimeSeriesStore>().PingAsync(ct),
            p =>
            {
                p.Interval = TimeSpan.FromSeconds(10);
                p.DegradedLatency = TimeSpan.FromMilliseconds(250);
                p.UnhealthyLatency = TimeSpan.FromSeconds(2);
            })
        // The consumer group's lag, as a plain number: the conditions look at how it moves.
        .AddProbe("telemetry-lag",
            (sp, ct) => sp.GetRequiredService<IIngestionStats>().GetLagAsync(ct),
            p => p.Interval = TimeSpan.FromSeconds(30))

        // The broker keeps readings for 7 days: pausing loses nothing, writing to a store that is down loses batches.
        .AddCondition("timeseries-store-down", c => c
            .ForSource("timeseries-store")
            .SustainedFor(TimeSpan.FromSeconds(30))
            .ClearAfter(TimeSpan.FromMinutes(2))
            .Heal(p => p.PauseConsumer(Consumer)))

        // A slow store still takes writes: shed the readings nobody needs in real time.
        .AddCondition("timeseries-store-slow", c => c
            .ForSource("timeseries-store")
            .WhenSeverityAtLeast(SignalSeverity.Degraded)
            .SustainedFor(TimeSpan.FromMinutes(2))
            .ClearAfter(TimeSpan.FromMinutes(5))
            .SuppressedBy("timeseries-store-down")
            .Heal(p => p.TurnOnSwitch(SkipDiagnosticReadings)))

        // A lag that keeps growing while the store is fine means the service writes too slowly for the traffic.
        // While the store is down or slow the lag grows because of it: those conditions heal the cause.
        .AddCondition("telemetry-lag-growing", c => c
            .ForSource("telemetry-lag")
            .WhenRisingFasterThan(2_000, TimeSpan.FromMinutes(10))
            .ClearAfter(TimeSpan.FromMinutes(15))
            .SuppressedBy("timeseries-store-down", "timeseries-store-slow")
            .Heal(p => p
                .TurnOnSwitch(LargerWriteBatches)
                .HoldAtMost(TimeSpan.FromHours(1))));
}
