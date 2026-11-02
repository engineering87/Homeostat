namespace Homeostat.Healing.Actions;

internal sealed class DelegateHealingAction : IHealingAction
{
    private readonly Func<HealingContext, CancellationToken, Task> _action;

    public DelegateHealingAction(Func<HealingContext, CancellationToken, Task> action) => _action = action;

    public Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken)
        => _action(context, cancellationToken);
}
