using Homeostat.Conditions;
using Homeostat.Engine;
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
    private readonly IHomeostatSwitches _switches;
    private readonly HomeostatApprovals _approvals;
    private readonly ILogger<ConditionReloader> _logger;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly object _gate = new();

    public ConditionReloader(
        ConditionSet conditions,
        IEnumerable<ConditionDefinition> codeDefinitions,
        IEnumerable<ConfigurationConditionSource> sources,
        SwitchOwnership ownership,
        IHomeostatSwitches switches,
        HomeostatApprovals approvals,
        ILogger<ConditionReloader> logger)
    {
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
            var before = _conditions.Trackers.Select(t => t.Definition.Name).ToHashSet(StringComparer.Ordinal);
            var definitions = _codeDefinitions.Concat(_sources.SelectMany(s => s.Load())).ToList();
            _conditions.Replace(definitions);

            // A removed condition will never clear: release what it holds.
            foreach (var removed in before.Except(definitions.Select(d => d.Name), StringComparer.Ordinal))
            {
                _approvals.CancelFor(removed);
                foreach (var name in _ownership.Release(removed))
                {
                    if (_switches.IsOn(name))
                    {
                        _switches.TurnOff(name);
                        _logger.LogInformation("Condition {Condition} was removed from configuration: switch {Switch} turned off.", removed, name);
                    }
                }
            }

            _logger.LogInformation("Homeostat conditions reloaded from configuration ({Count} conditions).", definitions.Count);
        }
        catch (Exception ex)
        {
            // Keep running with the previous conditions rather than with a broken configuration.
            _logger.LogError(ex, "The Homeostat configuration is not valid; keeping the previous conditions.");
        }
    }
}
