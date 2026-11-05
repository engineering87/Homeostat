using System.Diagnostics;
using System.Globalization;
using Homeostat.Signals;
using Microsoft.Extensions.Options;

namespace Homeostat.Detectors;

/// <summary>Settings for the thread pool starvation detector.</summary>
public sealed class ThreadPoolDetectorOptions
{
    /// <summary>Signal source name. Defaults to <c>"threadpool"</c>.</summary>
    public string Source { get; set; } = "threadpool";

    /// <summary>Queue latency at or above which the signal is <see cref="SignalSeverity.Degraded"/>.</summary>
    public TimeSpan DegradedLatency { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Queue latency at or above which the signal is <see cref="SignalSeverity.Unhealthy"/>.</summary>
    public TimeSpan UnhealthyLatency { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Measures how long a work item waits in the thread pool queue before it runs. A long wait means
/// the pool is starved, typically by blocking calls (sync over async), and every request slows down.
/// </summary>
/// <remarks>
/// Latency is measured with the real clock (<see cref="Stopwatch"/>) on purpose: it describes real
/// scheduling, which simulated time cannot represent.
/// </remarks>
internal sealed class ThreadPoolStarvationDetector : ISignalDetector
{
    private readonly ThreadPoolDetectorOptions _options;
    private readonly TimeProvider _timeProvider;

    public ThreadPoolStarvationDetector(IOptions<ThreadPoolDetectorOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
    {
        // The work item records the moment it actually runs, so later scheduling hops do not inflate the result.
        var ranAt = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedAt = Stopwatch.GetTimestamp();
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => ((TaskCompletionSource<long>)state!).TrySetResult(Stopwatch.GetTimestamp()),
            ranAt);

        // Do not wait forever on a starved pool: past twice the unhealthy threshold the answer is clear.
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            var giveUp = Task.Delay(Guardrails.TimerLimits.Clamp(_options.UnhealthyLatency * 2), cts.Token);
            await Task.WhenAny(ranAt.Task, giveUp).ConfigureAwait(false);
            cts.Cancel();
        }

        cancellationToken.ThrowIfCancellationRequested();

        var latency = ranAt.Task.IsCompletedSuccessfully
            ? Stopwatch.GetElapsedTime(queuedAt, ranAt.Task.Result)
            : Stopwatch.GetElapsedTime(queuedAt);

        var severity = latency >= _options.UnhealthyLatency ? SignalSeverity.Unhealthy
            : latency >= _options.DegradedLatency ? SignalSeverity.Degraded
            : SignalSeverity.Healthy;

        signals.TryPublish(new Signal(_options.Source, severity, _timeProvider.GetUtcNow())
        {
            Value = latency.TotalMilliseconds,
            Message = $"Thread pool queue latency {latency.TotalMilliseconds:F0} ms.",
            Tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["origin"] = "detector",
                ["latency_ms"] = latency.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture),
                ["threads"] = ThreadPool.ThreadCount.ToString(CultureInfo.InvariantCulture),
                ["pending"] = ThreadPool.PendingWorkItemCount.ToString(CultureInfo.InvariantCulture),
            },
        });
    }
}
