using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Homeostat.Healing.Actions;

internal sealed class ReloadConfigurationAction : IHealingAction
{
    private readonly IConfiguration? _configuration;
    private readonly ILogger _logger;

    public ReloadConfigurationAction(IConfiguration? configuration, ILogger<ReloadConfigurationAction> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
    {
        if (_configuration is not IConfigurationRoot root)
        {
            throw new InvalidOperationException(
                "The registered IConfiguration does not support reloading (it is missing or is not an IConfigurationRoot).");
        }

        _logger.LogInformation("Reloading configuration.");
        root.Reload();
        return Task.CompletedTask;
    }
}
