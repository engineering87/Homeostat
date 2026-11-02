using Homeostat.Conditions;
using Homeostat.Healing;

namespace Homeostat;

/// <summary>
/// Receives lifecycle events, for example to send notifications or record an audit trail.
/// Implement only the methods you need. Register with <c>AddObserver&lt;T&gt;()</c>.
/// </summary>
/// <remarks>
/// Methods are called synchronously on Homeostat's threads, possibly concurrently: keep them thread-safe and fast and hand off slow work
/// (such as HTTP calls) to a queue. Exceptions are logged and ignored.
/// </remarks>
public interface IHealingObserver
{
    /// <summary>A condition became active.</summary>
    void OnConditionActivated(ConditionSnapshot condition)
    {
    }

    /// <summary>An active condition cleared.</summary>
    void OnConditionCleared(ConditionSnapshot condition)
    {
    }

    /// <summary>A step finished (action executed, then verified if it succeeded). Not called in dry-run.</summary>
    void OnStepCompleted(HealingStepResult step)
    {
    }

    /// <summary>A healing run ended, whatever the outcome.</summary>
    void OnRunCompleted(HealingRunResult run)
    {
    }

    /// <summary>A step is waiting for approval: notify the people who can approve it.</summary>
    void OnApprovalRequested(PendingApproval approval)
    {
    }

    /// <summary>An approval request was approved, rejected or expired.</summary>
    void OnApprovalResolved(ApprovalResolution resolution)
    {
    }
}
