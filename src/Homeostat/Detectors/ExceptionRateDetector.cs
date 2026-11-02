using System.Globalization;
using System.Runtime.ExceptionServices;
using Homeostat.Signals;
using Microsoft.Extensions.Options;

namespace Homeostat.Detectors;

/// <summary>Settings for the exception rate detector.</summary>
public sealed class ExceptionRateDetectorOptions
{
    /// <summary>Signal source name.</summary>
    public string Source { get; set; } = "exceptions";

    /// <summary>Exceptions per second at or above which the signal is <see cref="SignalSeverity.Degraded"/>.</summary>
    public double DegradedPerSecond { get; set; } = 10;

    /// <summary>Exceptions per second at or above which the signal is <see cref="SignalSeverity.Unhealthy"/>.</summary>
    public double UnhealthyPerSecond { get; set; } = 50;
}

/// <summary>
/// Counts exceptions thrown in the process (first-chance, including handled ones) and reports the rate
/// since the previous sample. Tune the thresholds to your application's normal baseline.
/// </summary>
internal sealed class ExceptionRateDetector : ISignalDetector, IDisposable
{
    private readonly ExceptionRateDetectorOptions _options;
    private readonly TimeProvider _timeProvider;
    private long _count;
    private long _lastCount;
    private long _lastTimestamp;

    public ExceptionRateDetector(IOptions<ExceptionRateDetectorOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _lastTimestamp = timeProvider.GetTimestamp();
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
    }

    public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetTimestamp();
        var elapsed = _timeProvider.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        if (elapsed <= 0)
        {
            return ValueTask.CompletedTask;
        }

        var count = Interlocked.Read(ref _count);
        var rate = (count - _lastCount) / elapsed;
        _lastCount = count;
        _lastTimestamp = now;

        var severity = rate >= _options.UnhealthyPerSecond ? SignalSeverity.Unhealthy
            : rate >= _options.DegradedPerSecond ? SignalSeverity.Degraded
            : SignalSeverity.Healthy;

        signals.TryPublish(new Signal(_options.Source, severity, _timeProvider.GetUtcNow())
        {
            Value = rate,
            Message = $"{rate:F1} exceptions per second.",
            Tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["origin"] = "detector",
                ["rate_per_second"] = rate.ToString("F2", CultureInfo.InvariantCulture),
            },
        });

        return ValueTask.CompletedTask;
    }

    public void Dispose() => AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;

    private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e) => Interlocked.Increment(ref _count);
}
