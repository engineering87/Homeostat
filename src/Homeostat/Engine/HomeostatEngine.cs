using Homeostat.Conditions;
using Homeostat.Signals;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homeostat.Engine;

/// <summary>
/// Background loop: reads signals, updates conditions and starts healing runs.
/// </summary>
internal sealed class HomeostatEngine : BackgroundService
{
    private readonly SignalChannel _channel;
    private readonly ConditionSet _conditions;
    private readonly HealingRunner _runner;
    private readonly ObserverNotifier _notifier;
    private readonly HomeostatSwitches _switches;
    private readonly SwitchOwnership _ownership;
    private readonly HomeostatApprovals _approvals;
    private readonly ILogger<HomeostatEngine> _logger;
    private readonly Diagnostics.ActivationLog _activations;
    private readonly Recording.SignalRecorder[] _recorders;
    private readonly TimeProvider _timeProvider;
    private readonly Diagnostics.ConditionHistory _history;
    private readonly SeasonalBaselineKeeper _keeper;

    public HomeostatEngine(
        SignalChannel channel,
        ConditionSet conditions,
        HealingRunner runner,
        ObserverNotifier notifier,
        HomeostatSwitches switches,
        SwitchOwnership ownership,
        HomeostatApprovals approvals,
        ILogger<HomeostatEngine> logger,
        Healing.Actions.ApplicationClock clock,
        Diagnostics.ActivationLog activations,
        IEnumerable<Recording.SignalRecorder> recorders,
        TimeProvider timeProvider,
        Diagnostics.ConditionHistory history,
        SeasonalBaselineKeeper keeper)
    {
        _history = history;
        _keeper = keeper;
        _activations = activations;
        _recorders = recorders.ToArray();
        _timeProvider = timeProvider;
        // Resolved here, when the host starts, so the clock records the application's start time.
        _ = clock;
        _channel = channel;
        _conditions = conditions;
        _runner = runner;
        _notifier = notifier;
        _switches = switches;
        _ownership = ownership;
        _approvals = approvals;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_conditions.Trackers.Count == 0)
        {
            _logger.LogWarning("Homeostat started without any registered condition.");
        }


        try
        {
            // Seasonal rules compare with baselines saved by earlier runs: wait until they are loaded (the keeper
            // starts first, but hosts can start services concurrently). Signals queue meanwhile.
            await _keeper.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);

            await foreach (var signal in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    foreach (var recorder in _recorders)
                    {
                        recorder.Record(signal);
                    }

                    Process(signal, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One malformed signal must not stop self-healing for the whole application.
                    _logger.LogError(ex, "A signal from {Source} could not be processed.", signal.Source);
                }

                _channel.MarkProcessed();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Never: an exception out of a background service stops the whole application by default
            // (BackgroundServiceExceptionBehavior.StopHost). Self-healing stops; the application keeps running.
            _logger.LogCritical(ex, "The Homeostat engine stopped unexpectedly; self-healing is off until the application restarts.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Complete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Runs observe the stopping token, so they finish quickly; the wait is bounded by the host's shutdown token.
        await Task.WhenAny(_runner.WhenIdleAsync(), Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }

    private void ResetSwitches(ConditionDefinition definition)
    {
        // Only switches this condition turned on, and that no other active condition still holds.
        foreach (var name in _ownership.Release(definition.Name))
        {
            if (_switches.IsOn(name))
            {
                _switches.TurnOffLocal(name);
                _logger.LogInformation("Condition {Condition} cleared: switch {Switch} turned off.", definition.Name, name);
            }
        }

        // Application switches its cluster plan turned on: the store records the owner, so any instance can do it.
        if (definition.Plan?.Scope == Healing.PlanScope.Cluster)
        {
            _ = ReleaseSharedAsync(definition.Name);
        }
    }

    private async Task ReleaseSharedAsync(string condition)
    {
        try
        {
            foreach (var name in await _switches.ReleaseSharedAsync(condition, CancellationToken.None).ConfigureAwait(false))
            {
                _logger.LogInformation("Condition {Condition} cleared: application switch {Switch} turned off.", condition, name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Condition {Condition} cleared, but its application switches could not be turned off in the store.", condition);
        }
    }

    private void Process(Signal signal, CancellationToken stoppingToken)
    {
        // Arrays and for loops: this runs for every signal, without allocating.
        var trackers = _conditions.SignalTrackersFor(signal.Source);
        for (var i = 0; i < trackers.Length; i++)
        {
            Handle(trackers[i], signal, stoppingToken);
        }

        // Composite conditions follow the conditions they combine, in dependency order.
        var composites = _conditions.CompositeTrackers;
        for (var i = 0; i < composites.Length; i++)
        {
            var composite = composites[i];
            var definition = composite.Definition;
            var holds = _conditions.CompositeHolds(definition);
            var texts = _compositeTexts.GetValue(definition, static d => new CompositeTexts(
                ConditionBuilder.CompositeSource(d.Name), $"{d.Composite} of {string.Join(", ", d.DependsOn)} active."));
            var synthetic = new Signal(texts.Source, holds ? SignalSeverity.Unhealthy : SignalSeverity.Healthy, signal.Timestamp)
            {
                Message = holds ? texts.Message : null,
            };
            Handle(composite, synthetic, stoppingToken);
        }
    }

    // Per definition, so a reloaded definition gets its own and an old one is collected with it.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ConditionDefinition, CompositeTexts> _compositeTexts = new();

    private sealed record CompositeTexts(string Source, string Message);

    private void Handle(ConditionTracker tracker, Signal signal, CancellationToken stoppingToken)
    {
        ConditionTransition transition;
        try
        {
            transition = tracker.Observe(signal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Condition {Condition} failed to evaluate a signal from {Source}.", tracker.Definition.Name, signal.Source);
            return;
        }

        switch (transition)
        {
            case ConditionTransition.Activated:
            {
                _logger.LogWarning(
                    "Condition {Condition} is active (source {Source}, severity {Severity}).",
                    tracker.Definition.Name, signal.Source, signal.Severity);
                var snapshot = _conditions.Snapshot(tracker);
                var now = _timeProvider.GetUtcNow();
                _activations.Record(tracker.Definition.Name, now);
                _history.Record(tracker.Definition.Name, Persistence.ConditionEventKind.Activated, signal, now);
                _notifier.Notify(o => o.OnConditionActivated(snapshot));
                if (snapshot.SuppressedBy is { } cause && tracker.Definition.Plan is not null)
                {
                    _logger.LogInformation(
                        "Condition {Condition} is suppressed by {Cause}: not healing while {Cause} is active.",
                        tracker.Definition.Name, cause, cause);
                }

                _runner.TryStart(tracker, signal, stoppingToken);
                break;
            }

            case ConditionTransition.StillActive:
                // Lets a plan run again after its cooldown if the problem persists.
                _runner.TryStart(tracker, signal, stoppingToken);
                break;

            case ConditionTransition.Cleared:
            {
                _logger.LogInformation("Condition {Condition} cleared.", tracker.Definition.Name);
                _history.Record(tracker.Definition.Name, Persistence.ConditionEventKind.Cleared, signal, _timeProvider.GetUtcNow());
                _approvals.CancelFor(tracker.Definition.Name);
                _runner.OnConditionCleared(tracker.Definition);
                ResetSwitches(tracker.Definition);
                var snapshot = _conditions.Snapshot(tracker);
                _notifier.Notify(o => o.OnConditionCleared(snapshot));
                break;
            }
        }
    }
}
