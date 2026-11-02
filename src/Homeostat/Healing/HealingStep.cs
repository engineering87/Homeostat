namespace Homeostat.Healing;

/// <summary>One step of a <see cref="HealingPlan"/>: a named action resolved when the step runs.</summary>
public sealed class HealingStep
{
    internal HealingStep(string name, Func<IServiceProvider, IHealingAction> resolve)
    {
        Name = name;
        Resolve = resolve;
    }

    /// <summary>Name used in logs, metrics and run results.</summary>
    public string Name { get; }

    /// <summary>Whether a person must approve the step before it runs (see <see cref="HealingPlanBuilder.RequireApproval"/>).</summary>
    public bool RequiresApproval { get; internal set; }

    /// <summary>How long to wait for approval; <c>null</c> means <see cref="HomeostatOptions.DefaultApprovalTimeout"/>.</summary>
    public TimeSpan? ApprovalTimeout { get; internal set; }

    /// <summary>Switch turned off automatically when the condition clears, if the step turned it on.</summary>
    internal string? SwitchToResetOnClear { get; init; }

    /// <summary>Name of the compensating action, if any (see <see cref="HealingPlanBuilder.UndoWith{TAction}"/>).</summary>
    public string? UndoName { get; internal set; }

    internal Func<IServiceProvider, IHealingAction> Resolve { get; }

    internal Func<IServiceProvider, IHealingAction>? ResolveUndo { get; set; }
}
