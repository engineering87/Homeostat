using Homeostat.Healing;

namespace Homeostat.Samples.WeatherFeed;

/// <summary>Step 1: throw away the stuck client and create a fresh one.</summary>
public sealed class RenewFeedClientAction(WeatherFeedClient client, ILogger<RenewFeedClientAction> logger)
    : IHealingAction
{
    public Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("Renewing the weather feed client.");
        client.Renew();
        return Task.CompletedTask;
    }
}

/// <summary>Step 2 (escalation): fail over to the backup weather provider.</summary>
public sealed class SwitchToBackupProviderAction(WeatherFeedClient client, ILogger<SwitchToBackupProviderAction> logger)
    : IHealingAction
{
    public Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
    {
        logger.LogWarning("Switching the weather feed to the backup provider.");
        client.SwitchToBackup();
        return Task.CompletedTask;
    }
}
