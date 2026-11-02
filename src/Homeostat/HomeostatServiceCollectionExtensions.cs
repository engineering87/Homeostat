using Homeostat.Conditions;
using Homeostat.Coordination;
using Homeostat.Persistence;
using Homeostat.Detectors;
using Homeostat.Engine;
using Homeostat.Guardrails;
using Homeostat.Healing;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Homeostat;

/// <summary>Adds Homeostat to an <c>IServiceCollection</c>.</summary>
public static class HomeostatServiceCollectionExtensions
{
    /// <summary>
    /// Adds Homeostat and its background engine. Safe to call more than once:
    /// core services are registered only the first time.
    /// </summary>
    public static HomeostatBuilder AddHomeostat(this IServiceCollection services, Action<HomeostatBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<HomeostatOptions>()
            .Validate(o => o.MaxActionsPerWindow > 0, "MaxActionsPerWindow must be positive.")
            .Validate(o => o.ActionBudgetWindow > TimeSpan.Zero, "ActionBudgetWindow must be positive.")
            .Validate(o => o.MaxConcurrentHealings > 0, "MaxConcurrentHealings must be positive.")
            .Validate(o => o.DefaultCooldown >= TimeSpan.Zero, "DefaultCooldown cannot be negative.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.DefaultActionTimeout), "DefaultActionTimeout must be positive and at most about 49 days.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.DefaultVerificationTimeout), "DefaultVerificationTimeout must be positive and at most about 49 days.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.VerificationPollInterval), "VerificationPollInterval must be positive and at most about 49 days.")
            .Validate(o => o.SignalBufferCapacity > 0, "SignalBufferCapacity must be positive.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.DefaultApprovalTimeout), "DefaultApprovalTimeout must be positive and at most about 49 days.")
            .Validate(o => o.RunHistorySize >= 0, "RunHistorySize cannot be negative.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.InstanceId), "InstanceId cannot be empty.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.ClusterLeaseDuration) && o.ClusterLeaseDuration >= TimeSpan.FromSeconds(1), "ClusterLeaseDuration must be at least one second.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.DetectionInterval), "DetectionInterval must be positive and at most about 49 days.");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SignalChannel>();
        services.TryAddSingleton<ISignalPublisher>(sp => sp.GetRequiredService<SignalChannel>());
        services.TryAddSingleton(sp => new ConditionSet(
            sp.GetServices<ConditionDefinition>()
                .Concat(sp.GetServices<Configuration.ConfigurationConditionSource>().SelectMany(s => s.Load()))));
        services.TryAddSingleton<IHomeostatStore, InMemoryHomeostatStore>();
        services.TryAddSingleton<MaintenanceWindows>();
        services.TryAddSingleton<IHomeostatCoordinator, InMemoryCoordinator>();
        services.TryAddSingleton<HomeostatControl>();
        services.TryAddSingleton<IHomeostatControl>(sp => sp.GetRequiredService<HomeostatControl>());
        services.TryAddSingleton<IHomeostatSwitches, HomeostatSwitches>();
        services.TryAddSingleton<ObserverNotifier>();
        services.TryAddSingleton<SwitchOwnership>();
        services.TryAddSingleton<HomeostatApprovals>();
        services.TryAddSingleton<IHomeostatApprovals>(sp => sp.GetRequiredService<HomeostatApprovals>());
        services.TryAddSingleton<HealingRunner>();
        services.TryAddTransient<ConditionClearedVerifier>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HomeostatEngine>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DetectorHost>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, Diagnostics.HomeostatGauges>());

        var builder = new HomeostatBuilder(services);
        configure?.Invoke(builder);
        return builder;
    }
}
