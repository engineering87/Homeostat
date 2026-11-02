namespace Homeostat.Healing;

/// <summary>A step waiting for a person to approve it.</summary>
/// <param name="Id">Identifier to pass to <see cref="IHomeostatApprovals.Approve"/> or <see cref="IHomeostatApprovals.Reject"/>.</param>
/// <param name="ConditionName">Condition being healed.</param>
/// <param name="Step">Zero-based index of the step.</param>
/// <param name="StepName">Step name.</param>
/// <param name="RequestedAt">When the request was created.</param>
/// <param name="ExpiresAt">When the request expires; the run then stops with <see cref="HealingRunOutcome.NotApproved"/>.</param>
/// <param name="TriggeringSignal">The signal that started the run, to give the approver some context.</param>
public sealed record PendingApproval(
    string Id,
    string ConditionName,
    int Step,
    string StepName,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    Signals.Signal TriggeringSignal);

/// <summary>How an approval request ended.</summary>
/// <remarks>Numbered from 1: the default value, 0, is none of them, so an unset value never reads as a success.</remarks>
public enum ApprovalStatus
{
    /// <summary>A person approved the step.</summary>
    Approved = 1,

    /// <summary>A person rejected the step.</summary>
    Rejected = 2,

    /// <summary>Nobody answered in time.</summary>
    Expired = 3,

    /// <summary>The request was withdrawn: the condition cleared or the application stopped.</summary>
    Cancelled = 4,
}

/// <summary>Outcome of an approval request.</summary>
/// <param name="Request">The request.</param>
/// <param name="Status">How it ended.</param>
/// <param name="ResolvedBy">Who approved or rejected it, if provided; <c>null</c> when expired or cancelled.</param>
/// <param name="ResolvedAt">When it ended.</param>
public sealed record ApprovalResolution(
    PendingApproval Request,
    ApprovalStatus Status,
    string? ResolvedBy,
    DateTimeOffset ResolvedAt);
