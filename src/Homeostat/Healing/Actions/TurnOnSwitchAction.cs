using Microsoft.Extensions.Logging;

namespace Homeostat.Healing.Actions;

/// <summary>
/// Turns on a switch: for this instance in a plan that runs on every instance, for the whole application in a plan
/// that runs once per cluster (<c>OncePerCluster</c>), owned by the condition when it is turned off as the condition
/// clears.
/// </summary>
internal sealed class TurnOnSwitchAction : IHealingAction
{
    private readonly HomeostatSwitches _switches;
    private readonly string _name;
    private readonly bool _ownedByCondition;
    private readonly ILogger _logger;

    public TurnOnSwitchAction(HomeostatSwitches switches, string name, bool ownedByCondition, ILogger<TurnOnSwitchAction> logger)
    {
        _switches = switches;
        _name = name;
        _ownedByCondition = ownedByCondition;
        _logger = logger;
    }

    public async Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
    {
        if (!context.IsClusterPlan)
        {
            _logger.LogWarning("Turning on switch {Switch} for this instance.", _name);
            await _switches.TurnOnLocal(_name).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning("Turning on switch {Switch} for the application.", _name);
        if (_ownedByCondition)
        {
            await _switches.TurnOnForConditionAsync(_name, context.ConditionName, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _switches.TurnOnAsync(_name, SwitchScope.Application, $"condition {context.ConditionName}", cancellationToken).ConfigureAwait(false);
        }
    }
}
