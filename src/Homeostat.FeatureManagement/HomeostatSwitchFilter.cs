using Microsoft.Extensions.Configuration;
using Microsoft.FeatureManagement;

namespace Homeostat.FeatureManagement;

/// <summary>
/// Feature filter <c>Homeostat.Switch</c>: enables a feature according to the state of a Homeostat switch. By default
/// the feature is enabled while the switch is off, so a healing plan that turns the switch on (with
/// <c>TurnOnSwitch</c>) turns the feature off, and it comes back when the switch is turned off.
/// </summary>
/// <remarks>
/// Parameters: <c>Switch</c> (required), the switch name; <c>EnabledWhen</c>, <c>Off</c> (default) or <c>On</c>.
/// Combine it with other filters using <c>"RequirementType": "All"</c> to keep, for example, a percentage rollout
/// while still letting Homeostat shed the feature.
/// </remarks>
/// <example>
/// <code>
/// "FeatureManagement": {
///   "ForecastMaps": {
///     "EnabledFor": [ { "Name": "Homeostat.Switch", "Parameters": { "Switch": "shed-forecast-maps" } } ]
///   }
/// }
/// </code>
/// </example>
[FilterAlias(Alias)]
public sealed class HomeostatSwitchFilter : IFeatureFilter, IFilterParametersBinder
{
    /// <summary>Name of the filter in configuration.</summary>
    public const string Alias = "Homeostat.Switch";

    private readonly IHomeostatSwitches _switches;

    /// <summary>Creates the filter.</summary>
    /// <param name="switches">Homeostat switches.</param>
    public HomeostatSwitchFilter(IHomeostatSwitches switches)
    {
        ArgumentNullException.ThrowIfNull(switches);
        _switches = switches;
    }

    /// <inheritdoc />
    public object BindParameters(IConfiguration parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var name = parameters["Switch"];
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException($"The {Alias} feature filter needs a 'Switch' parameter.");
        }

        var enabledWhen = parameters["EnabledWhen"];
        var whenOn = enabledWhen switch
        {
            null or "" => false,
            _ when string.Equals(enabledWhen, "Off", StringComparison.OrdinalIgnoreCase) => false,
            _ when string.Equals(enabledWhen, "On", StringComparison.OrdinalIgnoreCase) => true,
            _ => throw new InvalidOperationException($"The {Alias} feature filter: 'EnabledWhen' must be 'On' or 'Off', not '{enabledWhen}'."),
        };

        return new Settings(name, whenOn);
    }

    /// <inheritdoc />
    public Task<bool> EvaluateAsync(FeatureFilterEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings as Settings ?? (Settings)BindParameters(context.Parameters);
        return Task.FromResult(_switches.IsOn(settings.Switch) == settings.EnabledWhenOn);
    }

    private sealed record Settings(string Switch, bool EnabledWhenOn);
}
