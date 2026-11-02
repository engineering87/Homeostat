using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homeostat.Healing.Actions;

internal sealed class RestartHostedServiceAction<TService> : IHealingAction
    where TService : class, IHostedService
{
    private readonly IEnumerable<IHostedService> _hostedServices;
    private readonly ILogger _logger;

    public RestartHostedServiceAction(IEnumerable<IHostedService> hostedServices, ILogger<RestartHostedServiceAction<TService>> logger)
    {
        _hostedServices = hostedServices;
        _logger = logger;
    }

    public async Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
    {
        var service = _hostedServices.OfType<TService>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"{typeof(TService).Name} is not registered as a hosted service. Register it with AddHostedService<{typeof(TService).Name}>().");

        _logger.LogInformation("Stopping hosted service {Service}.", typeof(TService).Name);
        await service.StopAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Starting hosted service {Service}.", typeof(TService).Name);
        await service.StartAsync(cancellationToken).ConfigureAwait(false);
    }
}
