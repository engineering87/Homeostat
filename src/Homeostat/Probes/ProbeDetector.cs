using Homeostat.Detectors;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Probes;

/// <summary>
/// Starts the due probes at each detection interval and returns at once: each call publishes its signal when it
/// completes, so a slow probe delays neither the other probes nor the other detectors. A call that ignores
/// cancellation is abandoned at its timeout, and the probe is not called again until it ends, so a hanging dependency
/// never piles up calls.
/// </summary>
internal sealed class ProbeDetector : ISignalDetector
{
    private readonly ProbeState[] _probes;
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _detectionInterval;
    private readonly ILogger<ProbeDetector> _logger;

    public ProbeDetector(
        IEnumerable<ProbeRegistration> probes,
        IServiceScopeFactory scopes,
        TimeProvider timeProvider,
        IOptions<HomeostatOptions> options,
        ILogger<ProbeDetector> logger)
    {
        _probes = probes.Select(p => new ProbeState(p)).ToArray();
        _scopes = scopes;
        _timeProvider = timeProvider;
        _detectionInterval = options.Value.DetectionInterval;
        _logger = logger;
    }

    public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var probe in _probes)
        {
            // A run still waiting within its timeout is left alone: the probe is just slow, not stuck.
            if (probe.Run is { IsCompleted: false } || !IsDue(probe, now))
            {
                continue;
            }

            probe.LastStart = now;
            probe.Run = RunAsync(probe, signals, cancellationToken);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Completes when every started run has published its signal. For tests.</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_probes.Select(p => p.Run ?? Task.CompletedTask));

    private bool IsDue(ProbeState probe, DateTimeOffset now)
    {
        if (probe.LastStart is not { } last || probe.Registration.Options.Interval is not { } interval)
        {
            return true;
        }

        // Half a detection interval of tolerance, so timer jitter does not skip a whole tick.
        return now - last >= interval - (_detectionInterval / 2);
    }

    private async Task RunAsync(ProbeState probe, ISignalPublisher signals, CancellationToken stoppingToken)
    {
        // Leave DetectAsync immediately, even when the probe completes synchronously.
        await Task.Yield();

        var registration = probe.Registration;
        var options = registration.Options;

        if (probe.Call is { IsCompleted: false })
        {
            Publish(signals, registration, options.FailureSeverity, value: null, "still-running",
                $"The previous call of probe '{registration.Source}' has not completed yet.");
            return;
        }

        // One timer: the timeout cancels the call's token and ends the wait at the same moment, so an abandoned
        // call always sees cancellation. The token sources are disposed only when the call itself ends.
        var timeout = new CancellationTokenSource(options.Timeout, _timeProvider);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);
        var token = linked.Token; // Read before the call can complete and dispose the source.
        var call = CallInScopeAsync(registration, token);
        probe.Call = call;
        _ = call.ContinueWith(
            static (t, state) =>
            {
                _ = t.Exception; // Observed, also when the call was abandoned.
                var (first, second) = ((CancellationTokenSource, CancellationTokenSource))state!;
                first.Dispose();
                second.Dispose();
            },
            (linked, timeout), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            var value = await call.WaitAsync(token).ConfigureAwait(false);
            if (!double.IsFinite(value))
            {
                Publish(signals, registration, options.FailureSeverity, value: null, "failed",
                    $"Probe '{registration.Source}' returned {value}, which is not a finite number.");
                return;
            }

            Publish(signals, registration, options.Classify(value), value, "ok",
                $"Probe '{registration.Source}' measured {value:0.###}.");
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: nothing to report.
        }
        catch (Exception ex) when (IsTimeout(ex, timeout))
        {
            // Some providers report cancellation with their own exception (SqlClient: SqlException); classify by the timer.
            Publish(signals, registration, options.FailureSeverity, value: null, "timeout",
                $"Probe '{registration.Source}' did not complete within {options.Timeout}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Probe {Source} failed.", registration.Source);
            Publish(signals, registration, options.FailureSeverity, value: null, "failed", FailureMessage(registration.Source, ex), ex);
        }
    }

    private static bool IsTimeout(Exception ex, CancellationTokenSource timeout)
    {
        try
        {
            return timeout.IsCancellationRequested;
        }
        catch (ObjectDisposedException)
        {
            return ex is OperationCanceledException;
        }
    }

    /// <summary>
    /// Database exception messages can carry server names and user names, and signal messages reach the status
    /// endpoint and webhooks: those keep only the exception type, and the details go to the log.
    /// </summary>
    private static string FailureMessage(string source, Exception ex) => ex is System.Data.Common.DbException
        ? $"Probe '{source}' failed: {ex.GetType().Name} (details in the application log)."
        : $"Probe '{source}' failed: {ex.GetType().Name}: {ex.Message}";

    private async Task<double> CallInScopeAsync(ProbeRegistration registration, CancellationToken cancellationToken)
    {
        // Yield first, so a probe that blocks synchronously does not block the caller.
        await Task.Yield();
        var scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            return await registration.Measure(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Publish(
        ISignalPublisher signals,
        ProbeRegistration registration,
        SignalSeverity severity,
        double? value,
        string outcome,
        string message,
        Exception? error = null)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["origin"] = registration.Origin,
            ["outcome"] = outcome,
        };
        if (error is not null)
        {
            tags["error"] = error.GetType().Name;
            if (error is System.Data.Common.DbException { IsTransient: var transient })
            {
                tags["transient"] = transient ? "true" : "false";
            }
        }

        signals.TryPublish(new Signal(registration.Source, severity, _timeProvider.GetUtcNow())
        {
            Value = value,
            Message = message,
            Tags = tags,
        });
    }

    private sealed class ProbeState(ProbeRegistration registration)
    {
        public ProbeRegistration Registration { get; } = registration;

        public DateTimeOffset? LastStart { get; set; }

        /// <summary>The latest run: wait, classify, publish. Ends at the timeout at the latest.</summary>
        public Task? Run { get; set; }

        /// <summary>The latest call of the probe function, which may outlive its run when abandoned.</summary>
        public Task? Call { get; set; }
    }
}
