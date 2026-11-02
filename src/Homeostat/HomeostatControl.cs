using Homeostat.Conditions;
using Homeostat.Engine;
using Homeostat.Guardrails;
using Homeostat.Healing;
using Microsoft.Extensions.Options;

namespace Homeostat;

internal sealed class HomeostatControl : IHomeostatControl
{
    private readonly ConditionSet _conditions;
    private readonly RunHistory _history;
    private readonly ActionBudget _budget;
    private readonly TimeProvider _timeProvider;
    private volatile bool _paused;

    public HomeostatControl(
        ConditionSet conditions,
        RunHistory history,
        ActionBudget budget,
        TimeProvider timeProvider,
        IOptions<HomeostatOptions> options)
    {
        _conditions = conditions;
        _history = history;
        _budget = budget;
        _timeProvider = timeProvider;
        IsDryRun = options.Value.DryRun;
    }

    public bool IsPaused => _paused;

    public bool IsDryRun { get; }

    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    public IReadOnlyList<ConditionSnapshot> GetConditions()
        => _conditions.Trackers.Select(_conditions.Snapshot).ToArray();

    public IReadOnlyList<HealingRunResult> GetRecentRuns() => _history.GetRecent();

    public ActionBudgetStatus GetBudget() => _budget.GetStatus(_timeProvider.GetUtcNow());
}
