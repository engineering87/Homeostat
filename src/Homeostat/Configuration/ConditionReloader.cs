using Homeostat.Conditions;
using Homeostat.Engine;
using Homeostat.Signals;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Homeostat.Configuration;

/// <summary>Rebuilds the conditions defined in configuration when it changes, keeping the state of unchanged names.</summary>
internal sealed class ConditionReloader : IHostedService, IDisposable
{
    private readonly ConditionSet _conditions;
    private readonly IReadOnlyList<ConditionDefinition> _codeDefinitions;
    private readonly IReadOnlyList<ConfigurationConditionSource> _sources;
    private readonly SwitchOwnership _ownership;
    private readonly HomeostatSwitches _switches;
    private readonly HomeostatApprovals _approvals;
    private readonly ILogger<ConditionReloader> _logger;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _gate = new();
    private readonly Diagnostics.ConditionHistory _history;
    private readonly Conditions.SeasonalBaselineKeeper _keeper;
    private readonly TimeProvider _timeProvider;

    public ConditionReloader(
        ConditionSet conditions,
        IEnumerable<ConditionDefinition> codeDefinitions,
        IEnumerable<ConfigurationConditionSource> sources,
        SwitchOwnership ownership,
        HomeostatSwitches switches,
        HomeostatApprovals approvals,
        ILogger<ConditionReloader> logger,
        Diagnostics.ConditionHistory history,
        Conditions.SeasonalBaselineKeeper keeper,
        TimeProvider timeProvider)
    {
        _history = history;
        _keeper = keeper;
        _timeProvider = timeProvider;
        _conditions = conditions;
        _ownership = ownership;
        _switches = switches;
        _approvals = approvals;
        _codeDefinitions = codeDefinitions.ToArray();
        _sources = sources.ToArray();
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var source in _sources)
        {
            _subscriptions.Add(ChangeToken.OnChange(source.Section.GetReloadToken, Reload));
        }

        // The conditions were loaded when the engine was built: a change made since then raised no notification here.
        if (_sources.Count > 0)
        {
            Reload();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
    }

    internal void Reload()
    {
        // Change notifications can arrive concurrently (several sections, several providers).
        lock (_gate)
        {
            ReloadCore();
        }
    }

    private void ReloadCore()
    {
        try
        {
            var before = _conditions.Trackers.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);
            var definitions = _codeDefinitions.Concat(_sources.SelectMany(s => s.Load())).ToList();
            _conditions.Replace(definitions);

            // A removed condition will never clear: release what it holds.
            foreach (var removed in before.Keys.Except(definitions.Select(d => d.Name), StringComparer.Ordinal))
            {
                // Close it in the history, which would otherwise show it active forever.
                if (before[removed].Snapshot().Status == ConditionStatus.Active)
                {
                    _history.Record(removed, Persistence.ConditionEventKind.Cleared,
                        new Signal(removed, SignalSeverity.Healthy, _timeProvider.GetUtcNow()) { Message = "Removed from configuration." },
                        _timeProvider.GetUtcNow());
                }

                before[removed].Retire();
                _approvals.CancelFor(removed);
                foreach (var name in _ownership.Release(removed))
                {
                    if (_switches.TurnOffLocal(name))
                    {
                        _logger.LogInformation("Condition {Condition} was removed from configuration: switch {Switch} turned off.", removed, name);
                    }
                }

                _ = _switches.ReleaseSharedAsync(removed, CancellationToken.None, anyInstance: true).AsTask().ContinueWith(
                    t => _logger.LogError(t.Exception, "Condition {Condition} was removed, but its application switches could not be turned off.", removed),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            _logger.LogInformation("Homeostat conditions reloaded from configuration ({Count} conditions).", definitions.Count);

            // New seasonal rules load their baseline now, and replaced ones are saved, without waiting for the timer.
            _keeper.SyncSoon();
        }
        catch (Exception ex)
        {
            // Keep running with the previous conditions rather than with a broken configuration.
            _logger.LogError(ex, "The Homeostat configuration is not valid; keeping the previous conditions.");
        }
    }
}
