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
    private readonly Guardrails.HealingGroups _groups;

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
        SeasonalBaselineKeeper keeper,
        Guardrails.HealingGroups groups)
    {
        _groups = groups;
        _history = history;
        _keeper = keeper;
        _activations = activations;
        _recorders = recorders.ToArray();
        _timeProvider = timeProvider;
        _systemClock = ReferenceEquals(timeProvider, TimeProvider.System);
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

        // Allowed (conditions can come from configuration later), but often a typo in a group name: said once.
        var used = _conditions.Trackers.Select(t => t.Definition.Group).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var unused in _groups.Configured.Where(g => !used.Contains(g)).Order(StringComparer.Ordinal))
        {
            _logger.LogInformation("Healing group {Group} is configured, but no condition is in it yet.", unused);
        }


        try
        {
            // Seasonal rules compare with baselines saved by earlier runs: wait until they are loaded (the keeper
            // starts first, but hosts can start services concurrently). Signals queue meanwhile.
            await _keeper.Ready.WaitAsync(stoppingToken).ConfigureAwait(false);

            await foreach (var received in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                var signal = received;
                try
                {
                    signal = FromThePast(received);
                    foreach (var recorder in _recorders)
                    {
                        recorder.Record(signal);
                    }

                    Process(signal, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
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

            // Nobody would turn off what the conditions turned on (they no longer clear): undo it now, so the
            // application is not left degraded by a Homeostat that is no longer running.
            foreach (var tracker in _conditions.Trackers)
            {
                try
                {
                    ResetSwitches(tracker.Definition);
                }
                catch (Exception resetError)
                {
                    _logger.LogError(resetError, "Could not turn off the switches of condition {Condition}.", tracker.Definition.Name);
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Complete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Runs observe the stopping token, so they finish quickly; the wait is bounded by the host's shutdown token.
        await Task.WhenAny(_runner.WhenIdleAsync(), Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>How far ahead of this instance's clock a signal's timestamp may be before it is taken as clock skew.</summary>
    internal static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(1);

    private DateTimeOffset _lastSkewWarning = DateTimeOffset.MinValue;

    private readonly bool _systemClock;

    /// <summary>
    /// The signal, with a timestamp further in the future than <see cref="FutureTolerance"/> replaced by now. One
    /// timestamp from a clock that runs ahead would otherwise hold trend and anomaly windows, occurrence counts and the
    /// freshness of suppressions for as long as the skew; timestamps in the past are kept as they are. Only on the
    /// system clock: with a simulated one (HomeostatTestHost, a replay) the signals' timestamps drive the scenario, and
    /// tests publish hours of signals ahead of it on purpose.
    /// </summary>
    private Signal FromThePast(Signal signal)
    {
        if (!_systemClock)
        {
            return signal;
        }

        var now = _timeProvider.GetUtcNow();
        if (signal.Timestamp <= now + FutureTolerance)
        {
            return signal;
        }

        if (now - _lastSkewWarning >= TimeSpan.FromMinutes(1))
        {
            _lastSkewWarning = now;
            _logger.LogWarning(
                "A signal from {Source} is timestamped {Timestamp:O}, ahead of this instance's clock ({Now:O}): it is processed as of now. Check the clock of its source.",
                signal.Source, signal.Timestamp, now);
        }

        return signal with { Timestamp = now };
    }

    private void ResetSwitches(ConditionDefinition definition)
    {
        // Only switches this condition turned on, and that no other active condition still holds.
        foreach (var name in _ownership.Release(definition.Name))
        {
            if (_switches.TurnOffLocal(name))
            {
                _logger.LogInformation("Condition {Condition} cleared: switch {Switch} turned off.", definition.Name, name);
            }
        }

        // Application switches its cluster plan turned on here (the store records the owner). Whatever the plan's scope
        // now: a configuration reload may have changed it after the switch was turned on.
        _ = Task.Run(() => ReleaseSharedAsync(definition.Name), CancellationToken.None);
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
        // Arrays and for loops: this runs for every signal. Only a composite that holds or is not yet inactive gets a
        // signal of its own.
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
            if (!holds && composite.Status == ConditionStatus.Inactive)
            {
                continue;   // nothing to track: no signal needed (and none allocated) for every incoming signal
            }

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
