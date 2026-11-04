using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.FeatureManagement;

namespace Homeostat.FeatureManagement;

/// <summary>
/// Feature filter <c>Homeostat.Switch</c>: enables a feature according to the state of a Homeostat switch. By default
/// the feature is enabled while the switch is off, so a healing plan that turns the switch on (with
/// <c>TurnOnSwitch</c>) turns the feature off, and it comes back when the switch is turned off.
/// </summary>
/// <remarks>
/// Parameters: <c>Switch</c> (required), the switch name; <c>EnabledWhen</c>, <c>Off</c> (default) or <c>On</c>. Invalid
/// parameters never make the feature check throw (that would fail every request that checks the feature): they are
/// logged once, and the feature is enabled, as when the switch is off.
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
    private readonly ILogger _logger;
    private int _warned;

    /// <summary>Creates the filter.</summary>
    /// <param name="switches">Homeostat switches.</param>
    /// <param name="logger">Logs invalid parameters; optional.</param>
    public HomeostatSwitchFilter(IHomeostatSwitches switches, ILogger<HomeostatSwitchFilter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(switches);
        _switches = switches;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public object BindParameters(IConfiguration parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var name = parameters["Switch"];
        if (string.IsNullOrWhiteSpace(name))
        {
            return new Settings(null, false, $"The {Alias} feature filter needs a 'Switch' parameter.");
        }

        var enabledWhen = parameters["EnabledWhen"];
        return enabledWhen switch
        {
            null or "" => new Settings(name, false, null),
            _ when string.Equals(enabledWhen, "Off", StringComparison.OrdinalIgnoreCase) => new Settings(name, false, null),
            _ when string.Equals(enabledWhen, "On", StringComparison.OrdinalIgnoreCase) => new Settings(name, true, null),
            _ => new Settings(null, false, $"The {Alias} feature filter: 'EnabledWhen' must be 'On' or 'Off', not '{enabledWhen}'."),
        };
    }

    /// <inheritdoc />
    public Task<bool> EvaluateAsync(FeatureFilterEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = context.Settings as Settings ?? (Settings)BindParameters(context.Parameters);
        if (settings.Switch is not { } name)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                _logger.LogWarning("{Error} Feature {Feature} is enabled, as when the switch is off.", settings.Error, context.FeatureName);
            }

            return Task.FromResult(true);
        }

        return Task.FromResult(_switches.IsOn(name) == settings.EnabledWhenOn);
    }

    private sealed record Settings(string? Switch, bool EnabledWhenOn, string? Error);
}
