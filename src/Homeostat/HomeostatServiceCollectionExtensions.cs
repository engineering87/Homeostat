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
    /// Adds Homeostat and its background engine. Safe to call more than once: core services are registered only the
    /// first time, and every call can add conditions, detectors and integrations.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="configure">Adds conditions, healing plans, detectors, stores and integrations; optional.</param>
    /// <returns>The builder, to keep configuring Homeostat.</returns>
    /// <remarks>
    /// Homeostat starts in dry-run: it detects, decides and logs every step it would take, but runs none until
    /// <see cref="HomeostatOptions.DryRun"/> is set to <c>false</c>. Options are validated when the host starts.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddHealthChecks().AddCheck&lt;WeatherFeedHealthCheck&gt;("weather-feed");
    /// builder.Services.AddHomeostat(h => h
    ///     .Configure(o => o.DryRun = false)             // after reviewing the dry-run log
    ///     .AddHealthCheckSignals()                      // Homeostat.HealthChecks
    ///     .AddCondition("weather-feed-down", c => c
    ///         .ForSource("weather-feed")
    ///         .SustainedFor(TimeSpan.FromSeconds(30))
    ///         .Heal(p => p
    ///             .With&lt;RenewFeedClientAction&gt;()
    ///             .Then&lt;SwitchToBackupProviderAction&gt;().RequireApproval())));
    /// </code>
    /// </example>
    public static HomeostatBuilder AddHomeostat(this IServiceCollection services, Action<HomeostatBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<HomeostatOptions>()
            .Validate(o => o.MaxActionsPerWindow > 0, "MaxActionsPerWindow must be positive.")
            .Validate(o => o.ActionBudgetWindow > TimeSpan.Zero, "ActionBudgetWindow must be positive.")
            .Validate(o => o.MaxConcurrentHealings > 0, "MaxConcurrentHealings must be positive.")
            .Validate(o => o.DefaultCooldown >= TimeSpan.Zero && o.DefaultCooldown <= Guardrails.TimerLimits.Max, "DefaultCooldown cannot be negative or longer than about 49 days.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.DefaultActionTimeout), "DefaultActionTimeout must be positive and at most about 49 days.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.DefaultVerificationTimeout), "DefaultVerificationTimeout must be positive and at most about 49 days.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.VerificationPollInterval), "VerificationPollInterval must be positive and at most about 49 days.")
            .Validate(o => o.SignalBufferCapacity > 0, "SignalBufferCapacity must be positive.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.DefaultApprovalTimeout), "DefaultApprovalTimeout must be positive and at most about 49 days.")
            .Validate(o => o.RunHistorySize >= 0, "RunHistorySize cannot be negative.")
            .Validate(o => o.ConditionHistorySize >= 0, "ConditionHistorySize cannot be negative.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.InstanceId), "InstanceId cannot be empty.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.ClusterLeaseDuration) && o.ClusterLeaseDuration >= TimeSpan.FromSeconds(1), "ClusterLeaseDuration must be at least one second.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.DetectionInterval), "DetectionInterval must be positive and at most about 49 days.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.ControlRefreshInterval), "ControlRefreshInterval must be positive and at most about 49 days.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SignalChannel>();
        services.TryAddSingleton<ISignalPublisher>(sp => sp.GetRequiredService<SignalChannel>());
        services.TryAddSingleton(sp => new ConditionSet(
            sp.GetServices<ConditionDefinition>()
                .Concat(sp.GetServices<Configuration.ConfigurationConditionSource>().SelectMany(s => s.Load())),
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IHomeostatStore, InMemoryHomeostatStore>();
        services.TryAddSingleton<MaintenanceWindows>();
        services.TryAddSingleton<IHomeostatCoordinator, InMemoryCoordinator>();
        services.TryAddSingleton<HomeostatControl>();
        services.TryAddSingleton<IHomeostatControl>(sp => sp.GetRequiredService<HomeostatControl>());
        services.TryAddSingleton<SharedControl>();
        services.TryAddSingleton<PlanStateCache>();
        services.TryAddSingleton<HomeostatSwitches>();
        services.TryAddSingleton<IHomeostatSwitches>(sp => sp.GetRequiredService<HomeostatSwitches>());
        HomeostatConsumerExtensions.AddConsumerServices(services);
        services.TryAddSingleton<ObserverNotifier>();
        services.TryAddSingleton<SwitchOwnership>();
        services.TryAddSingleton<HomeostatApprovals>();
        services.TryAddSingleton<IHomeostatApprovals>(sp => sp.GetRequiredService<HomeostatApprovals>());
        services.TryAddSingleton<HealingRunner>();
        services.TryAddSingleton<Healing.Actions.ApplicationClock>();
        services.TryAddSingleton<Diagnostics.ActivationLog>();
        services.TryAddTransient<ConditionClearedVerifier>();
        services.TryAddSingleton<Diagnostics.ConditionHistory>();
        services.TryAddSingleton<Conditions.SeasonalBaselineKeeper>();

        // First, so the pause and the switches set before this instance started apply from its first signal.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SharedControlRefresher>());

        // Registered before the engine, so they stop after it: the last events and baselines are written.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, Diagnostics.ConditionHistory>(sp => sp.GetRequiredService<Diagnostics.ConditionHistory>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, Conditions.SeasonalBaselineKeeper>(sp => sp.GetRequiredService<Conditions.SeasonalBaselineKeeper>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HomeostatEngine>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DetectorHost>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, Diagnostics.HomeostatGauges>());

        var builder = new HomeostatBuilder(services);
        configure?.Invoke(builder);
        return builder;
    }
}
