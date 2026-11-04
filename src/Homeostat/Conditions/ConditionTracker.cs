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
    private DateTimeOffset? _lastTriggeredAt;
    private Signal? _lastSignal;
    private bool _retired;

    private volatile ConditionDefinition _definition;

    public ConditionTracker(ConditionDefinition definition) => _definition = definition;

    public ConditionDefinition Definition => _definition;

    /// <summary>Applies a new definition with the same name (configuration reload), keeping the current state.</summary>
    public void Update(ConditionDefinition definition)
    {
        lock (_gate)
        {
            _definition = definition;
        }
    }

    public ConditionTransition Observe(Signal signal)
    {
        var definition = Definition;
        if (!definition.IsRelevant(signal))
        {
            return ConditionTransition.None;
        }

        definition.Record?.Invoke(signal);
        var triggered = definition.IsTriggered(signal);
        var cleared = !triggered && definition.IsCleared(signal);

        lock (_gate)
        {
            if (_retired)
            {
                return ConditionTransition.None;
            }

            _lastSignalAt = signal.Timestamp;
            _lastSignal = signal;

            if (triggered)
            {
                _lastTriggeredAt = signal.Timestamp;
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
                if (wasActive && definition.ClearAfter > TimeSpan.Zero)
                {
                    // Hysteresis: recovery must hold for ClearAfter before the condition clears.
                    _clearingSince ??= signal.Timestamp;
                    if (signal.Timestamp - _clearingSince.Value < definition.ClearAfter)
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

    /// <summary>
    /// The condition was removed (configuration reload): it becomes inactive for good, so a run still in progress
    /// stops before its next step and releases what it turns on.
    /// </summary>
    public void Retire()
    {
        lock (_gate)
        {
            _retired = true;
            _status = ConditionStatus.Inactive;
            _pendingSince = null;
            _activeSince = null;
            _clearingSince = null;
        }
    }

    /// <summary>When a signal last met the trigger (a cause stays Pending only while such signals keep coming).</summary>
    public DateTimeOffset? LastTriggeredAt
    {
        get
        {
            lock (_gate)
            {
                return _lastTriggeredAt;
            }
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
