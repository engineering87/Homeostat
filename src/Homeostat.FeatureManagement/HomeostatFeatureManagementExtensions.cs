using Homeostat.FeatureManagement;
using Microsoft.FeatureManagement;

namespace Homeostat;

/// <summary>Registers the Homeostat feature filter.</summary>
public static class HomeostatFeatureManagementExtensions
{
    /// <summary>
    /// Adds the <c>Homeostat.Switch</c> feature filter (see <see cref="HomeostatSwitchFilter"/>), which enables a
    /// feature according to the state of a Homeostat switch.
    /// </summary>
    /// <param name="builder">The feature management builder.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddFeatureManagement().AddHomeostatSwitchFilter();
    /// builder.Services.AddHomeostat(h => h.AddCondition("catalog-slow", c => c
    ///     .ForSource("catalog-latency").SustainedFor(TimeSpan.FromMinutes(1))
    ///     .Heal(p => p.TurnOnSwitch("shed-recommendations", turnOffWhenCleared: true))));
    /// </code>
    /// </example>
    public static IFeatureManagementBuilder AddHomeostatSwitchFilter(this IFeatureManagementBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddFeatureFilter<HomeostatSwitchFilter>();
    }
}
