using Homeostat.Consumers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Homeostat.Samples.Architectures.TelemetryIngestion;

/// <summary>One reading sent by a device.</summary>
/// <param name="Device">Device id.</param>
/// <param name="Channel">"measurement" or "diagnostic".</param>
/// <param name="Value">The reading.</param>
public sealed record Reading(string Device, string Channel, double Value);

/// <summary>The time-series store (your code).</summary>
public interface ITimeSeriesStore
{
    /// <summary>Round trip to the store; throws when it cannot be reached.</summary>
    Task PingAsync(CancellationToken cancellationToken);

    /// <summary>Writes a batch of readings.</summary>
    Task WriteAsync(IReadOnlyList<Reading> readings, CancellationToken cancellationToken);
}

/// <summary>The broker client of the ingestion service (your code).</summary>
public interface ITelemetryReader
{
    /// <summary>Reads up to <paramref name="max"/> readings, waiting for at least one.</summary>
    Task<IReadOnlyList<Reading>> ReadAsync(int max, CancellationToken cancellationToken);

    /// <summary>Marks the readings returned so far as processed.</summary>
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>Statistics of the consumer group (your code, for example from the broker's admin API).</summary>
public interface IIngestionStats
{
    /// <summary>Readings published but not yet processed by the group.</summary>
    Task<double> GetLagAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The ingestion loop (your code). It waits on the pause gate before each batch and reads the switches the plans turn
/// on; it knows nothing else about Homeostat.
/// </summary>
public sealed class TelemetryIngestionWorker(
    [FromKeyedServices(TelemetryIngestionHealing.Consumer)] PauseGate gate,
    IHomeostatSwitches switches,
    ITelemetryReader reader,
    ITimeSeriesStore store) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await gate.WaitWhilePausedAsync(stoppingToken);

            var size = switches.IsOn(TelemetryIngestionHealing.LargerWriteBatches) ? 5_000 : 500;
            var readings = await reader.ReadAsync(size, stoppingToken);
            if (switches.IsOn(TelemetryIngestionHealing.SkipDiagnosticReadings))
            {
                readings = [.. readings.Where(r => r.Channel != "diagnostic")];
            }

            await store.WriteAsync(readings, stoppingToken);
            await reader.CommitAsync(stoppingToken);
        }
    }
}
