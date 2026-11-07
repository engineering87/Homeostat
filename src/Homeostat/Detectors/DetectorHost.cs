using Homeostat.Signals;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Detectors;

/// <summary>Runs every registered <see cref="ISignalDetector"/> at the configured interval.</summary>
internal sealed class DetectorHost : BackgroundService
{
    private readonly ISignalDetector[] _detectors;
    private readonly ISignalPublisher _signals;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DetectorHost> _logger;

    public DetectorHost(
        IEnumerable<ISignalDetector> detectors,
        ISignalPublisher signals,
        IOptions<HomeostatOptions> options,
        TimeProvider timeProvider,
        ILogger<DetectorHost> logger)
    {
        _detectors = detectors.ToArray();
        _signals = signals;
        _interval = options.Value.DetectionInterval;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Waits for one call, at most <see cref="CallLimit"/>, and logs when the detector starts or stops failing.</summary>
    private async Task WatchAsync(int index, Task call, bool[] failing, CancellationToken stoppingToken)
    {
        // Observed, so the eventual failure of a call no longer waited for is not an unobserved task exception.
        _ = call.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        Exception? error = null;
        try
        {
            await call.WaitAsync(CallLimit, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;   // Stopping.
        }
        catch (Exception ex)
        {
            error = ex;
        }

        var name = _detectors[index].GetType().Name;

        // Under the lock: the call of the next interval may report while this one does.
        lock (failing)
        {
            if (error is null)
            {
                if (failing[index])
                {
                    failing[index] = false;
                    _logger.LogInformation("Detector {Detector} works again.", name);
                }
            }
            else if (!failing[index])
            {
                // Once when it starts failing, not at every interval.
                failing[index] = true;
                _logger.LogError(error, error is TimeoutException
                    ? "Detector {Detector} did not complete in time; it is skipped until it does."
                    : "Detector {Detector} failed.", name);
            }
            else
            {
                _logger.LogDebug(error, "Detector {Detector} still fails.", name);
            }
        }
    }

    private TimeSpan CallLimit => _interval > TimeSpan.FromSeconds(30) ? _interval : TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_detectors.Length == 0)
        {
            return;
        }

        // Leave StartAsync immediately, whatever the hosting model: a slow detector must not delay host startup.
        await Task.Yield();

        // One call per detector at a time, each bounded (in real time, at least 30 seconds) and waited for on its own:
        // the detectors run concurrently, so a slow one does not delay the others, and one that never completes (a
        // blocking call, a lost token) is skipped until it does, instead of stopping every other detector.
        var pending = new Task?[_detectors.Length];
        var failing = new bool[_detectors.Length];

        try
        {
            // Inside the try: whatever the timer refuses stops the detectors, never the application.
            using var timer = new PeriodicTimer(_interval, _timeProvider);
            do
            {
                for (var i = 0; i < _detectors.Length; i++)
                {
                    if (pending[i] is { IsCompleted: false })
                    {
                        continue;
                    }

                    var detector = _detectors[i];
                    var call = Task.Run(() => detector.DetectAsync(_signals, stoppingToken).AsTask(), CancellationToken.None);
                    pending[i] = call;
                    _ = WatchAsync(i, call, failing, stoppingToken);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // An exception out of a background service would stop the application (StopHost): signals stop instead.
            _logger.LogCritical(ex, "The Homeostat detectors stopped unexpectedly; their signals are no longer published.");
        }
    }
}
