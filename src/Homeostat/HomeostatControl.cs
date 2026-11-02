using Homeostat.Conditions;
using Homeostat.Guardrails;
using Homeostat.Persistence;
using Microsoft.Extensions.Options;

namespace Homeostat;

internal sealed class HomeostatControl : IHomeostatControl
{
    private readonly ConditionSet _conditions;
    private readonly IHomeostatStore _store;
    private readonly MaintenanceWindows _maintenance;
    private readonly TimeProvider _timeProvider;
    private readonly HomeostatOptions _options;
    private readonly object _gate = new();
    private bool _paused;
    private DateTimeOffset? _pausedUntil;

    public HomeostatControl(
        ConditionSet conditions,
        IHomeostatStore store,
        MaintenanceWindows maintenance,
        TimeProvider timeProvider,
        IOptions<HomeostatOptions> options)
    {
        _conditions = conditions;
        _store = store;
        _maintenance = maintenance;
        _timeProvider = timeProvider;
        _options = options.Value;
    }

    public bool IsPaused => PauseReason is not null;

    public string? PauseReason
    {
        get
        {
            var now = _timeProvider.GetUtcNow();
            lock (_gate)
            {
                if (_paused)
                {
                    return "paused";
                }

                if (_pausedUntil is { } until && now < until)
                {
                    return $"paused until {until:O}";
                }
            }

            return _maintenance.ActiveWindow(now) is { } window ? $"maintenance window '{window}'" : null;
        }
    }

    public bool IsDryRun => _options.DryRun;

    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
        }
    }

    public void PauseUntil(DateTimeOffset until)
    {
        lock (_gate)
        {
            _pausedUntil = until;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            _pausedUntil = null;
        }
    }

    public IReadOnlyList<ConditionSnapshot> GetConditions()
        => _conditions.Trackers.Select(_conditions.Snapshot).ToArray();

    public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(CancellationToken cancellationToken = default)
        => _store.GetRecentRunsAsync(_options.RunHistorySize, cancellationToken);

    public async ValueTask<ActionBudgetStatus> GetBudgetAsync(CancellationToken cancellationToken = default)
    {
        var used = await _store.GetBudgetUsedAsync(_timeProvider.GetUtcNow(), _options.ActionBudgetWindow, cancellationToken).ConfigureAwait(false);
        return new ActionBudgetStatus(used, _options.MaxActionsPerWindow, _options.ActionBudgetWindow);
    }
}
