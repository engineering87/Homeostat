namespace Homeostat;

/// <summary>
/// The guardrails of a healing group (a named set of conditions, typically one module of a modular monolith, set with
/// <see cref="Conditions.ConditionBuilder.InGroup"/>), applied on top of the global ones in <see cref="HomeostatOptions"/>.
/// Configure them with <see cref="HomeostatBuilder.ConfigureGroup"/>. Every setting is optional: a group without any
/// still has its label and its own pause.
/// </summary>
/// <remarks>
/// Read once when Homeostat starts and validated then (positive values, timer limits): changing the instance afterwards
/// has no effect.
/// </remarks>
public sealed class HealingGroupOptions
{
    /// <summary>
    /// Healing steps the conditions of this group may execute within <see cref="ActionBudgetWindow"/>, inside the global
    /// <see cref="HomeostatOptions.MaxActionsPerWindow"/>: a step consumes one unit of this budget first, then one of the
    /// global budget. When it is spent, the runs of this group stop with <c>BudgetExhausted</c> while the other groups go
    /// on. <c>null</c> (the default): the group has no budget of its own.
    /// </summary>
    public int? MaxActionsPerWindow { get; set; }

    /// <summary>
    /// Sliding window of <see cref="MaxActionsPerWindow"/>; <c>null</c> (the default) uses the global
    /// <see cref="HomeostatOptions.ActionBudgetWindow"/>.
    /// </summary>
    public TimeSpan? ActionBudgetWindow { get; set; }

    /// <summary>
    /// Runs of this group executing at the same time, inside the global <see cref="HomeostatOptions.MaxConcurrentHealings"/>:
    /// a run takes a slot of its group first, then a global slot, so a busy group never holds a global slot that another
    /// group could use. <c>null</c> (the default): no limit of its own.
    /// </summary>
    public int? MaxConcurrentHealings { get; set; }
}
