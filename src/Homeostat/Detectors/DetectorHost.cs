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

    private TimeSpan CallLimit => _interval > TimeSpan.FromSeconds(30) ? _interval : TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_detectors.Length == 0)
        {
            return;
        }

        // Leave StartAsync immediately, whatever the hosting model: a slow detector must not delay host startup.
        await Task.Yield();

        // One call per detector at a time, each bounded (in real time, at least 30 seconds): a detector that never completes
        // (a blocking call, a lost token) is skipped until it does, instead of stopping every other detector.
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
                    var detector = _detectors[i];
                    if (pending[i] is { IsCompleted: false })
                    {
                        continue;
                    }

                    try
                    {
                        var call = Task.Run(() => detector.DetectAsync(_signals, stoppingToken).AsTask(), CancellationToken.None);
                        pending[i] = call;
                        await call.WaitAsync(CallLimit, stoppingToken).ConfigureAwait(false);
                        if (failing[i])
                        {
                            failing[i] = false;
                            _logger.LogInformation("Detector {Detector} works again.", detector.GetType().Name);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        // Once when it starts failing, not at every interval.
                        if (!failing[i])
                        {
                            failing[i] = true;
                            _logger.LogError(ex, ex is TimeoutException
                                ? "Detector {Detector} did not complete in time; it is skipped until it does."
                                : "Detector {Detector} failed.", detector.GetType().Name);
                        }
                        else
                        {
                            _logger.LogDebug(ex, "Detector {Detector} still fails.", detector.GetType().Name);
                        }

                        if (pending[i] is { } abandoned)
                        {
                            _ = abandoned.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        }
                    }
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
