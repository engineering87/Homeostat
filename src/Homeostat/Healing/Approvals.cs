namespace Homeostat.Healing;

/// <summary>A step waiting for a person to approve it.</summary>
public sealed record PendingApproval
{
    /// <summary>
    /// Identifier to pass to <see cref="IHomeostatApprovals.ApproveAsync"/> or <see cref="IHomeostatApprovals.RejectAsync"/>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>Condition being healed.</summary>
    public required string ConditionName { get; init; }

    /// <summary>Zero-based index of the step.</summary>
    public required int Step { get; init; }

    /// <summary>Step name.</summary>
    public required string StepName { get; init; }

    /// <summary>When the request was created.</summary>
    public required DateTimeOffset RequestedAt { get; init; }

    /// <summary>When the request expires; the run then stops with <see cref="HealingRunOutcome.NotApproved"/>.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The signal that started the run, to give the approver some context.</summary>
    public required Signals.Signal TriggeringSignal { get; init; }
}

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
    Canceled = 4,
}

/// <summary>Outcome of an approval request.</summary>
public sealed record ApprovalResolution
{
    /// <summary>The request.</summary>
    public required PendingApproval Request { get; init; }

    /// <summary>How it ended.</summary>
    public required ApprovalStatus Status { get; init; }

    /// <summary>Who approved or rejected it, if provided; <c>null</c> when expired or canceled.</summary>
    public string? ResolvedBy { get; init; }

    /// <summary>When it ended.</summary>
    public required DateTimeOffset ResolvedAt { get; init; }
}
