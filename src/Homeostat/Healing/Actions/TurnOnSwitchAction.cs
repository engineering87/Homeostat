using Microsoft.Extensions.Logging;

namespace Homeostat.Healing.Actions;

internal sealed class TurnOnSwitchAction : IHealingAction
{
    private readonly IHomeostatSwitches _switches;
    private readonly string _name;
    private readonly ILogger _logger;

    public TurnOnSwitchAction(IHomeostatSwitches switches, string name, ILogger<TurnOnSwitchAction> logger)
    {
        _switches = switches;
        _name = name;
        _logger = logger;
    }

    public Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Turning on switch {Switch}.", _name);
        _switches.TurnOn(_name);
        return Task.CompletedTask;
    }
}
