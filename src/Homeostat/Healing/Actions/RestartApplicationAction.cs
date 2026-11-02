using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homeostat.Healing.Actions;

/// <summary>When this application instance started, as seen by Homeostat (when the host starts its services).</summary>
internal sealed class ApplicationClock(TimeProvider timeProvider)
{
    public DateTimeOffset StartedAt { get; } = timeProvider.GetUtcNow();
}

/// <summary>Stops the application with an exit code, so the orchestrator or service manager starts a fresh process.</summary>
internal sealed class RestartApplicationAction(
    IHostApplicationLifetime? lifetime,
    RestartLimits limits,
    int exitCode,
    ILogger<RestartApplicationAction> logger) : IHealingAction
{
    public async Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
    {
        if (lifetime is null)
        {
            throw new InvalidOperationException("RestartApplication needs IHostApplicationLifetime: run the application with the generic host.");
        }

        await RestartGuard.EnsureAllowedAsync(context, limits, cancellationToken).ConfigureAwait(false);

        logger.LogCritical(
            "Stopping the application with exit code {ExitCode} for condition {Condition}: the orchestrator is expected to start a new process.",
            exitCode, context.ConditionName);
        Environment.ExitCode = exitCode;
        lifetime.StopApplication();
    }
}
