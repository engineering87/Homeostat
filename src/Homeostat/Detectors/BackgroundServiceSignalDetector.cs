using System.Globalization;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Homeostat.Detectors;

/// <summary>
/// Publishes the state of every registered <see cref="RestartableBackgroundService"/>: Healthy while its run is going,
/// Unhealthy when the run threw or returned and the service waits to be restarted, nothing while it is starting,
/// restarting or stopping. Source: <c>hosted-service:{type name}</c> by default.
/// </summary>
internal sealed class BackgroundServiceSignalDetector : ISignalDetector
{
    private readonly IServiceProvider _provider;
    private readonly TimeProvider _timeProvider;
    private readonly string _prefix;
    private RestartableBackgroundService[]? _services;

    // The hosted services are resolved on first use: the detector host is itself a hosted service.
    public BackgroundServiceSignalDetector(IServiceProvider provider, TimeProvider timeProvider, Microsoft.Extensions.Options.IOptions<BackgroundServiceSignalOptions> options)
    {
        _provider = provider;
        _timeProvider = timeProvider;
        _prefix = string.IsNullOrWhiteSpace(options.Value.SourcePrefix) ? "hosted-service" : options.Value.SourcePrefix;
    }

    public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
    {
        _services ??= _provider.GetServices<IHostedService>().OfType<RestartableBackgroundService>().ToArray();
        var now = _timeProvider.GetUtcNow();
        foreach (var service in _services)
        {
            var name = service.GetType().Name;
            var running = service.IsRunning;
            if (!running && !service.IsIdle)
            {
                continue; // Not started yet, restarting or stopping: not a failure.
            }

            var tags = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["origin"] = "background-service",
                ["restarts"] = service.Restarts.ToString(CultureInfo.InvariantCulture),
            };
            if (service.LastError is { } error)
            {
                tags["error"] = error.GetType().Name;
            }

            signals.TryPublish(new Signal($"{_prefix}:{name}", running ? SignalSeverity.Healthy : SignalSeverity.Unhealthy, now)
            {
                Value = service.Restarts,
                Message = running ? $"{name} is running."
                    : service.LastError is { } e ? $"{name} stopped with {e.GetType().Name} (details in the application log)."
                    : $"{name} is not running.",
                Tags = tags,
            });
        }

        return ValueTask.CompletedTask;
    }
}
