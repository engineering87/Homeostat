using System.Globalization;
using Homeostat.Signals;

namespace Homeostat.Calls;

/// <summary>
/// Records the outcome and duration of the calls to one dependency, and turns each detection interval into a signal:
/// the failure ratio as its value, with the 95th percentile duration in its tags. Use it for any client Homeostat has
/// no ready-made integration for: an SDK, a TCP protocol, a legacy service. Register it with
/// <see cref="HomeostatCallSignalExtensions.AddCallSignals(HomeostatBuilder, string, Action{CallSignalOptions}?)"/> and
/// resolve it with <c>GetRequiredKeyedService&lt;CallRecorder&gt;(source)</c>.
/// </summary>
/// <remarks>Thread-safe and cheap: recording takes a lock for a few instructions and allocates nothing in most calls.</remarks>
public sealed class CallRecorder
{
    private const int MaxLatencySamples = 2048;

    private readonly object _gate = new();
    private readonly List<double> _latenciesMs = [];
    private readonly TimeProvider _timeProvider;
    private int _total;
    private int _failures;
    private int _timed;

    /// <summary>Creates a recorder. Usually created by <c>AddCallSignals</c>; create one yourself to unit test code that uses it, or to report without dependency injection.</summary>
    /// <param name="source">Source of the signals.</param>
    /// <param name="options">Thresholds; the defaults when <c>null</c>.</param>
    /// <param name="timeProvider">Clock used by <c>TrackAsync</c>; the system clock when <c>null</c>.</param>
    public CallRecorder(string source, CallSignalOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Source = source;
        // A copy: changing the instance passed in later does not change a running recorder.
        Options = (options ?? new CallSignalOptions()).Clone();
        Options.Validate(source);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Source of the signals published for these calls.</summary>
    public string Source { get; }

    /// <summary>The thresholds (a copy of those passed to the constructor).</summary>
    internal CallSignalOptions Options { get; }

    /// <summary>Records one call.</summary>
    /// <param name="failed">Whether the call failed because of the dependency (not because of a wrong request).</param>
    /// <param name="duration">How long the call took.</param>
    public void Record(bool failed, TimeSpan duration)
    {
        lock (_gate)
        {
            Count(failed);
            _timed++;
            if (_latenciesMs.Count < MaxLatencySamples)
            {
                _latenciesMs.Add(duration.TotalMilliseconds);
            }
            else
            {
                // Reservoir sampling: every timed call of the interval has the same chance to be kept, so the p95
                // reflects the whole interval and not only its beginning.
                var slot = Random.Shared.NextInt64(_timed);
                if (slot < MaxLatencySamples)
                {
                    _latenciesMs[(int)slot] = duration.TotalMilliseconds;
                }
            }
        }
    }

    /// <summary>
    /// Records one call without a duration: it counts for the failure ratio but not for the latency percentile. Use it
    /// for calls whose duration says nothing about the dependency, such as a stream kept open by the caller.
    /// </summary>
    /// <param name="failed">Whether the call failed because of the dependency (not because of a wrong request).</param>
    public void Record(bool failed)
    {
        lock (_gate)
        {
            Count(failed);
        }
    }

    private void Count(bool failed)
    {
        _total++;
        if (failed)
        {
            _failures++;
        }
    }

    /// <summary>
    /// Runs <paramref name="call"/>, records its duration, and records it as failed when it throws an exception that
    /// <paramref name="isFailure"/> accepts (by default any exception except a cancellation requested through
    /// <paramref name="cancellationToken"/>). The exception is rethrown.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">Passed to the call.</param>
    /// <param name="isFailure">Decides which exceptions count as failures of the dependency; optional.</param>
    /// <returns>The task of the call.</returns>
    /// <example><code>await recorder.TrackAsync(ct => geocoder.LookupAsync(address, ct), cancellationToken: cancellationToken);</code></example>
    public async Task TrackAsync(Func<CancellationToken, Task> call, Func<Exception, bool>? isFailure = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        await TrackAsync<bool>(async ct => { await call(ct).ConfigureAwait(false); return true; }, isFailure, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc cref="TrackAsync(Func{CancellationToken, Task}, Func{Exception, bool}?, CancellationToken)"/>
    /// <typeparam name="T">The result type.</typeparam>
    /// <returns>The result of the call.</returns>
    public async Task<T> TrackAsync<T>(Func<CancellationToken, Task<T>> call, Func<Exception, bool>? isFailure = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        var started = _timeProvider.GetTimestamp();
        try
        {
            var result = await call(cancellationToken).ConfigureAwait(false);
            Record(false, _timeProvider.GetElapsedTime(started));
            return result;
        }
        catch (Exception ex)
        {
            var callerCanceled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            if (!callerCanceled)
            {
                Record(IsFailure(isFailure, ex), _timeProvider.GetElapsedTime(started));
            }

            throw;
        }
    }

    // The caller must get its own exception back: a classifier that throws counts the call as failed instead.
    private static bool IsFailure(Func<Exception, bool>? isFailure, Exception exception)
    {
        try
        {
            return isFailure?.Invoke(exception) ?? true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>The signal for the calls recorded since the previous collection, or <c>null</c> when there is nothing to say.</summary>
    internal Signal? Collect(DateTimeOffset now, string origin)
    {
        double[] latencies;
        int total;
        int failures;
        lock (_gate)
        {
            total = _total;
            failures = _failures;
            latencies = _latenciesMs.ToArray();
            _total = 0;
            _failures = 0;
            _timed = 0;
            _latenciesMs.Clear();
        }

        return Evaluate(Source, Options, origin, total, failures, Percentile95(latencies), now);
    }

    internal static Signal? Evaluate(string source, CallSignalOptions options, string origin, int total, int failures, TimeSpan? p95, DateTimeOffset now)
    {
        if (total == 0)
        {
            return null;
        }

        var ratio = (double)failures / total;
        SignalSeverity severity;
        if (total < options.MinimumCalls)
        {
            // Too few calls to judge a ratio: only a clean interval says something.
            if (failures > 0)
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
            if (p95 is { } latency)
            {
                if (options.UnhealthyLatency is { } unhealthy && latency >= unhealthy)
                {
                    severity = SignalSeverity.Unhealthy;
                }
                else if (options.DegradedLatency is { } degraded && latency >= degraded && severity < SignalSeverity.Degraded)
                {
                    severity = SignalSeverity.Degraded;
                }
            }
        }

        var p95Text = p95 is { } value ? value.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) : "n/a";
        return new Signal(source, severity, now)
        {
            Value = ratio,
            Message = $"{failures}/{total} calls failed, p95 {p95Text} ms.",
            Tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["origin"] = origin,
                ["calls"] = total.ToString(CultureInfo.InvariantCulture),
                ["failures"] = failures.ToString(CultureInfo.InvariantCulture),
                ["failure_ratio"] = ratio.ToString("F3", CultureInfo.InvariantCulture),
                ["p95_ms"] = p95Text,
            },
        };
    }

    private static TimeSpan? Percentile95(double[] latenciesMs)
    {
        if (latenciesMs.Length == 0)
        {
            return null;
        }

        Array.Sort(latenciesMs);
        var index = (int)Math.Ceiling(0.95 * latenciesMs.Length) - 1;
        return TimeSpan.FromMilliseconds(latenciesMs[Math.Clamp(index, 0, latenciesMs.Length - 1)]);
    }
}
