using System.Diagnostics.CodeAnalysis;
using Homeostat.Configuration;
using Homeostat.Healing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Homeostat;

/// <summary>Defines conditions and plans in configuration.</summary>
public static class HomeostatConfigurationExtensions
{
    /// <summary>
    /// Gives an action class a name that conditions defined in configuration can use, as <c>"Action": "name"</c>
    /// or <c>"UndoAction": "name"</c>.
    /// </summary>
    /// <typeparam name="TAction">The action class, registered as transient.</typeparam>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="name">Name used in configuration, for example <c>"renew-feed-client"</c>.</param>
    public static HomeostatBuilder AddAction<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TAction>(this HomeostatBuilder builder, string name)
        where TAction : class, IHealingAction
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        builder.Services.TryAddTransient<TAction>();
        Catalog(builder).Map(name, p => p.With<TAction>(), p => p.UndoWith<TAction>());
        return builder;
    }

    /// <summary>
    /// Adds the conditions defined in a configuration section, one child per condition, and rebuilds them when the
    /// configuration changes (for example when <c>appsettings.json</c> is edited), keeping the state of conditions
    /// whose name did not change. An invalid configuration fails at startup; an invalid change is logged and ignored.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="section">The section, for example <c>builder.Configuration.GetSection("Homeostat:Conditions")</c>.</param>
    /// <example>
    /// <code>
    /// "Homeostat": {
    ///   "Conditions": {
    ///     "weather-feed-down": {
    ///       "Source": "weather-feed",
    ///       "SustainedFor": "30s",
    ///       "ClearAfter": "2m",
    ///       "Plan": {
    ///         "Steps": [
    ///           { "Action": "renew-feed-client" },
    ///           { "Action": "switch-to-backup", "RequireApproval": true, "UndoAction": "switch-to-primary" }
    ///         ],
    ///         "Cooldown": "5m"
    ///       }
    ///     }
    ///   }
    /// }
    /// </code>
    /// </example>
    public static HomeostatBuilder AddConditionsFromConfiguration(this HomeostatBuilder builder, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(section);

        // The catalog is registered now: after the host is built, the service collection is read-only.
        var catalog = Catalog(builder);
        builder.Services.AddSingleton(sp => new ConfigurationConditionSource(section, catalog));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ConditionReloader>());
        return builder;
    }

    private static ConfiguredActions Catalog(HomeostatBuilder builder)
    {
        var descriptor = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(ConfiguredActions));
        if (descriptor?.ImplementationInstance is ConfiguredActions existing)
        {
            return existing;
        }

        var catalog = new ConfiguredActions();
        builder.Services.AddSingleton(catalog);
        return catalog;
    }
}
