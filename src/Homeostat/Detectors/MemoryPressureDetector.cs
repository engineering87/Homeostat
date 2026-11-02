using System.Globalization;
using Homeostat.Signals;
using Microsoft.Extensions.Options;

namespace Homeostat.Detectors;

/// <summary>Settings for the memory pressure detector.</summary>
public sealed class MemoryPressureDetectorOptions
{
    /// <summary>Signal source name.</summary>
    public string Source { get; set; } = "memory";

    /// <summary>Memory load (0 to 1) at or above which the signal is <see cref="SignalSeverity.Degraded"/>.</summary>
    public double DegradedLoad { get; set; } = 0.85;

    /// <summary>Memory load (0 to 1) at or above which the signal is <see cref="SignalSeverity.Unhealthy"/>.</summary>
    public double UnhealthyLoad { get; set; } = 0.95;
}

/// <summary>
/// Reports memory load as seen by the garbage collector: memory in use relative to the memory available
/// to the process (the container limit, when there is one). Values refresh at every garbage collection.
/// </summary>
internal sealed class MemoryPressureDetector : ISignalDetector
{
    private readonly MemoryPressureDetectorOptions _options;
    private readonly TimeProvider _timeProvider;

    public MemoryPressureDetector(IOptions<MemoryPressureDetectorOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
    {
        var info = GC.GetGCMemoryInfo();
        if (info.TotalAvailableMemoryBytes <= 0)
        {
            // No garbage collection has happened yet: nothing reliable to report.
            return ValueTask.CompletedTask;
        }

        var load = (double)info.MemoryLoadBytes / info.TotalAvailableMemoryBytes;
        var severity = load >= _options.UnhealthyLoad ? SignalSeverity.Unhealthy
            : load >= _options.DegradedLoad ? SignalSeverity.Degraded
            : SignalSeverity.Healthy;

        signals.TryPublish(new Signal(_options.Source, severity, _timeProvider.GetUtcNow())
        {
            Value = load,
            Message = $"Memory load {load:P0}.",
            Tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["origin"] = "detector",
                ["load"] = load.ToString("F3", CultureInfo.InvariantCulture),
                ["heap_bytes"] = GC.GetTotalMemory(forceFullCollection: false).ToString(CultureInfo.InvariantCulture),
                ["gc_pause_percent"] = info.PauseTimePercentage.ToString("F1", CultureInfo.InvariantCulture),
            },
        });

        return ValueTask.CompletedTask;
    }
}
