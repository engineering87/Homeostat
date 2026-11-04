using System.Globalization;
using Homeostat.Detectors;
using Homeostat.Signals;

namespace Homeostat.Http;

/// <summary>Publishes one signal per instrumented client at every detection interval.</summary>
internal sealed class HttpClientSignalDetector : ISignalDetector
{
    private readonly HttpClientSignalCatalog _catalog;
    private readonly TimeProvider _timeProvider;

    public HttpClientSignalDetector(HttpClientSignalCatalog catalog, TimeProvider timeProvider)
    {
        _catalog = catalog;
        _timeProvider = timeProvider;
    }

    public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var window in _catalog.Windows)
        {
            var signal = Evaluate(window.Source, window.Options, window.Collect(), now);
            if (signal is not null)
            {
                signals.TryPublish(signal);
            }
        }

        return ValueTask.CompletedTask;
    }

    internal static Signal? Evaluate(string source, HttpClientSignalOptions options, HttpWindowSample sample, DateTimeOffset now)
    {
        if (sample.Total == 0)
        {
            return null;
        }

        var ratio = (double)sample.Failures / sample.Total;
        SignalSeverity severity;

        if (sample.Total < options.MinimumCalls)
        {
            // Too few requests to judge a ratio: only a clean sample says something.
            if (sample.Failures > 0)
            {
                return null;
            }

            severity = SignalSeverity.Healthy;
        }
        else
        {
            severity = ratio >= options.UnhealthyFailureRatio ? SignalSeverity.Unhealthy
                : ratio >= options.DegradedFailureRatio ? SignalSeverity.Degraded
                : SignalSeverity.Healthy;

            if (sample.P95Latency is { } p95)
            {
                if (options.UnhealthyLatency is { } unhealthy && p95 >= unhealthy)
                {
                    severity = SignalSeverity.Unhealthy;
                }
                else if (options.DegradedLatency is { } degraded && p95 >= degraded && severity < SignalSeverity.Degraded)
                {
                    severity = SignalSeverity.Degraded;
                }
            }
        }

        var p95Text = sample.P95Latency is { } latency
            ? latency.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)
            : "n/a";

        return new Signal(source, severity, now)
        {
            Value = ratio,
            Message = $"{sample.Failures}/{sample.Total} requests failed, p95 {p95Text} ms.",
            Tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["origin"] = "http-client",
                ["requests"] = sample.Total.ToString(CultureInfo.InvariantCulture),
                ["failures"] = sample.Failures.ToString(CultureInfo.InvariantCulture),
                ["failure_ratio"] = ratio.ToString("F3", CultureInfo.InvariantCulture),
                ["p95_ms"] = p95Text,
            },
        };
    }
}
