using Homeostat.Signals;

namespace Homeostat.Conditions;

internal enum ConditionTransition
{
    None,
    Activated,

    /// <summary>A triggering signal arrived while the condition was already active.</summary>
    StillActive,
    Cleared,
}

/// <summary>
/// Thread-safe state machine for one condition. Signals are applied by the engine loop;
/// snapshots may be read concurrently by healing runs.
/// </summary>
internal sealed class ConditionTracker
{
    private readonly object _gate = new();
    private ConditionStatus _status = ConditionStatus.Inactive;
    private DateTimeOffset? _pendingSince;
    private DateTimeOffset? _activeSince;
    private DateTimeOffset? _clearingSince;
    private DateTimeOffset? _lastSignalAt;
    private Signal? _lastSignal;

    public ConditionTracker(ConditionDefinition definition) => Definition = definition;

    public ConditionDefinition Definition { get; }

    public ConditionTransition Observe(Signal signal)
    {
        if (!Definition.IsRelevant(signal))
        {
            return ConditionTransition.None;
        }

        var triggered = Definition.IsTriggered(signal);
        var cleared = !triggered && Definition.IsCleared(signal);

        lock (_gate)
        {
            _lastSignalAt = signal.Timestamp;
            _lastSignal = signal;

            if (triggered)
            {
                switch (_status)
                {
                    case ConditionStatus.Inactive:
                        _status = ConditionStatus.Pending;
                        _pendingSince = signal.Timestamp;
                        return TryActivate(signal.Timestamp);

                    case ConditionStatus.Pending:
                        return TryActivate(signal.Timestamp);

                    default:
                        _clearingSince = null;
                        return ConditionTransition.StillActive;
                }
            }

            if (cleared)
            {
                var wasActive = _status == ConditionStatus.Active;
                if (wasActive && Definition.ClearAfter > TimeSpan.Zero)
                {
                    // Hysteresis: recovery must hold for ClearAfter before the condition clears.
                    _clearingSince ??= signal.Timestamp;
                    if (signal.Timestamp - _clearingSince.Value < Definition.ClearAfter)
                    {
                        return ConditionTransition.None;
                    }
                }

                _status = ConditionStatus.Inactive;
                _pendingSince = null;
                _activeSince = null;
                _clearingSince = null;
                return wasActive ? ConditionTransition.Cleared : ConditionTransition.None;
            }

            return ConditionTransition.None;
        }
    }

    public ConditionSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ConditionSnapshot(Definition.Name, _status, _pendingSince, _activeSince, _lastSignalAt, _lastSignal) { ClearingSince = _clearingSince };
        }
    }

    // Caller holds the lock.
    private ConditionTransition TryActivate(DateTimeOffset now)
    {
        if (_pendingSince is { } since && now - since >= Definition.SustainFor)
        {
            _status = ConditionStatus.Active;
            _activeSince = now;
            return ConditionTransition.Activated;
        }

        return ConditionTransition.None;
    }
}
