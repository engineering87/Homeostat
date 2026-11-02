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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_detectors.Length == 0)
        {
            return;
        }

        // Leave StartAsync immediately, whatever the hosting model: a slow detector must not delay host startup.
        await Task.Yield();

        using var timer = new PeriodicTimer(_interval, _timeProvider);

        try
        {
            do
            {
                foreach (var detector in _detectors)
                {
                    try
                    {
                        await detector.DetectAsync(_signals, stoppingToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogError(ex, "Detector {Detector} failed.", detector.GetType().Name);
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }
}
