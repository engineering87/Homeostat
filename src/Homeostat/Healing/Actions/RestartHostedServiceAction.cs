using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homeostat.Healing.Actions;

internal sealed class RestartHostedServiceAction<TService> : IHealingAction
    where TService : class, IHostedService
{
    private readonly IEnumerable<IHostedService> _hostedServices;
    private readonly IServiceProvider _services;
    private readonly ILogger _logger;

    public RestartHostedServiceAction(
        IEnumerable<IHostedService> hostedServices,
        IServiceProvider services,
        ILogger<RestartHostedServiceAction<TService>> logger)
    {
        _hostedServices = hostedServices;
        _services = services;
        _logger = logger;
    }

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    public async Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
    {
        var service = _hostedServices.OfType<TService>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"{typeof(TService).Name} is not registered as a hosted service. Register it with AddHostedService<{typeof(TService).Name}>().");

        if (service is RestartableBackgroundService restartable)
        {
            _logger.LogInformation("Restarting background service {Service}.", typeof(TService).Name);
            await restartable.RestartAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (service is BackgroundService && !HostIgnoresBackgroundServiceFailures(_services))
        {
            throw new InvalidOperationException(
                $"{typeof(TService).Name} is a BackgroundService: stopping it outside a shutdown makes the generic host treat it as " +
                $"failed and stop the application (HostOptions.BackgroundServiceExceptionBehavior is StopHost). Derive it from " +
                $"RestartableBackgroundService, which Homeostat restarts safely.");
        }

        _logger.LogInformation("Stopping hosted service {Service}.", typeof(TService).Name);
        try
        {
            await service.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Whatever happened to the stop (it threw, or the step's timeout canceled it), the service is started again:
            // a restart must never leave it stopped. The start gets its own deadline, as the step's may have passed.
            _logger.LogInformation("Starting hosted service {Service}.", typeof(TService).Name);
            using var start = new CancellationTokenSource(StartTimeout);
            await service.StartAsync(start.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether the generic host is configured with <c>BackgroundServiceExceptionBehavior.Ignore</c>. HostOptions lives in
    /// Microsoft.Extensions.Hosting, which Homeostat does not reference, so it is read by name. The check is
    /// conservative: when the type is available, the default (StopHost) applies unless Ignore is configured.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "HostOptions is read by reflection to avoid a dependency on Microsoft.Extensions.Hosting. If trimming removed the property, the value reads as null and the restart is refused: the safe default.")]
    internal static bool HostIgnoresBackgroundServiceFailures(IServiceProvider services)
    {
        var hostOptionsType = Type.GetType("Microsoft.Extensions.Hosting.HostOptions, Microsoft.Extensions.Hosting", throwOnError: false);
        if (hostOptionsType is null)
        {
            return true;
        }

        var options = services.GetService(typeof(Microsoft.Extensions.Options.IOptions<>).MakeGenericType(hostOptionsType));
        var value = options?.GetType().GetProperty("Value")?.GetValue(options);
        var behavior = value?.GetType().GetProperty("BackgroundServiceExceptionBehavior")?.GetValue(value);
        return string.Equals(behavior?.ToString(), "Ignore", StringComparison.Ordinal);
    }
}
