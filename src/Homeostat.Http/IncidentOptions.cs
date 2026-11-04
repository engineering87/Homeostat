using Homeostat.Notifications;

namespace Homeostat.Http;

/// <summary>
/// When Homeostat opens and resolves incidents; shared by the PagerDuty and Opsgenie notifiers. These are decisions,
/// not event kinds (an incident opens when healing gives up, a run outcome; it resolves when the condition clears), so
/// they are flags rather than an <c>Events</c> set as on the chat and webhook notifiers.
/// </summary>
public abstract class IncidentOptions
{
    /// <summary>Only the built-in options derive from this class: a derived class could not be registered.</summary>
    private protected IncidentOptions()
    {
    }

    /// <summary>
    /// Opens an incident when healing gives up and a person is needed: the run ended as not healed, inconclusive,
    /// out of budget or not approved. Defaults to <c>true</c>.
    /// </summary>
    public bool OnHealingGaveUp { get; set; } = true;

    /// <summary>Opens an incident when a step waits for approval, so the on-call person is paged. Defaults to <c>true</c>.</summary>
    public bool OnApprovalRequested { get; set; } = true;

    /// <summary>Opens an incident as soon as a condition becomes active, before any healing. Defaults to <c>false</c>.</summary>
    public bool OnConditionActivated { get; set; }

    /// <summary>Resolves the incident of a condition when the condition clears. Defaults to <c>true</c>.</summary>
    public bool ResolveWhenCleared { get; set; } = true;

    /// <summary>Timeout of one delivery attempt. Defaults to 10 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Delivery attempts, including the first. Defaults to 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    internal static readonly HomeostatEventKind[] Kinds =
    [
        HomeostatEventKind.ConditionActivated, HomeostatEventKind.ConditionCleared,
        HomeostatEventKind.RunCompleted, HomeostatEventKind.ApprovalRequested,
    ];

    /// <summary>
    /// Decides what to send, given whether the condition is known to have cleared. A run's completion can reach the
    /// sink after the condition cleared (the run is persisted first): a late trigger would open an incident that
    /// nothing resolves, so it is skipped.
    /// </summary>
    internal IncidentDecision Decide(HomeostatEvent e, bool knownCleared)
        => e.Kind is HomeostatEventKind.RunCompleted or HomeostatEventKind.ApprovalRequested && knownCleared
            ? IncidentDecision.None
            : Decide(e);

    internal IncidentDecision Decide(HomeostatEvent e) => e.Kind switch
    {
        HomeostatEventKind.ConditionActivated when OnConditionActivated => IncidentDecision.Trigger(IncidentReason.Activated),
        HomeostatEventKind.ApprovalRequested when OnApprovalRequested => IncidentDecision.Trigger(IncidentReason.ApprovalNeeded),
        HomeostatEventKind.RunCompleted when OnHealingGaveUp && e.Outcome is "NotHealed" or "Inconclusive" or "BudgetExhausted" or "NotApproved"
            => IncidentDecision.Trigger(IncidentReason.GaveUp),
        HomeostatEventKind.ConditionCleared when ResolveWhenCleared => IncidentDecision.Resolve,
        _ => IncidentDecision.None,
    };

    /// <summary>
    /// One incident per application and condition: triggers update it, clearing resolves it. Dots, not slashes: the
    /// Opsgenie alias goes in a URL path, where an encoded slash is often rejected.
    /// </summary>
    internal static string Key(HomeostatEvent e)
    {
        var key = $"homeostat.{e.Application}.{e.Condition}".Replace('/', '-').Replace('\\', '-');
        return key.Length <= 255 ? key : key[..255];
    }

    internal virtual void Validate()
    {
        if (Timeout <= TimeSpan.Zero || MaxAttempts < 1)
        {
            throw new ArgumentException("Timeout must be positive and MaxAttempts at least 1.");
        }
    }
}

internal enum IncidentReason
{
    Activated,
    ApprovalNeeded,
    GaveUp,
}

internal readonly record struct IncidentDecision(bool IsTrigger, bool IsResolve, IncidentReason Reason)
{
    public static IncidentDecision None => default;

    public static IncidentDecision Resolve => new(false, true, default);

    public static IncidentDecision Trigger(IncidentReason reason) => new(true, false, reason);
}
