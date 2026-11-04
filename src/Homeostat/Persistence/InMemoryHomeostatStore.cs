using Homeostat.Healing;

namespace Homeostat.Persistence;

/// <summary>Default store: everything in process memory, lost on restart, not shared between instances.</summary>
public sealed class InMemoryHomeostatStore : IHomeostatStore
{
    private readonly MemoryStore _store = new();

    /// <inheritdoc />
    public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => _store.GetPlanStateAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) => _store.UpdatePlanStateAsync(key, update, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _store.TryConsumeBudgetAsync(now, limit, window, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => _store.GetBudgetUsedAsync(now, window, cancellationToken);

    /// <inheritdoc />
    public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => _store.AppendRunAsync(run, maxRuns, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) => _store.GetRecentRunsAsync(maxRuns, cancellationToken);

    /// <inheritdoc />
    public ValueTask AppendConditionEventAsync(ConditionEventRecord conditionEvent, int maxEvents, CancellationToken cancellationToken = default) => _store.AppendConditionEventAsync(conditionEvent, maxEvents, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ConditionEventRecord>> GetConditionEventsAsync(int maxEvents, CancellationToken cancellationToken = default) => _store.GetConditionEventsAsync(maxEvents, cancellationToken);

    /// <inheritdoc />
    public ValueTask<string?> GetDocumentAsync(string key, CancellationToken cancellationToken = default) => _store.GetDocumentAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask SetDocumentAsync(string key, string document, CancellationToken cancellationToken = default) => _store.SetDocumentAsync(key, document, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) => _store.GetControlStateAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask SetPauseAsync(PauseState? pause, CancellationToken cancellationToken = default) => _store.SetPauseAsync(pause, cancellationToken);

    /// <inheritdoc />
    public ValueTask SetSwitchAsync(string name, SwitchState? state, CancellationToken cancellationToken = default) => _store.SetSwitchAsync(name, state, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) => _store.TryClaimSwitchAsync(state, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, CancellationToken cancellationToken = default)
        => _store.TryReleaseSwitchAsync(name, condition, instance, cancellationToken);

    /// <inheritdoc />
    public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) => _store.AddApprovalAsync(approval, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default) => _store.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken);

    /// <inheritdoc />
    public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) => _store.RemoveApprovalAsync(id, cancellationToken);

    /// <inheritdoc />
    public bool SupportsConditionHistory => true;
}
