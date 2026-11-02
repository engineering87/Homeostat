namespace Homeostat.Notifications;

/// <summary>Kinds of <see cref="HomeostatEvent"/>.</summary>
public enum HomeostatEventKind
{
    /// <summary>A condition became active.</summary>
    ConditionActivated,

    /// <summary>An active condition cleared.</summary>
    ConditionCleared,

    /// <summary>A healing step finished.</summary>
    StepCompleted,

    /// <summary>A healing run ended.</summary>
    RunCompleted,

    /// <summary>A step is waiting for approval.</summary>
    ApprovalRequested,

    /// <summary>An approval request was approved, rejected or expired.</summary>
    ApprovalResolved,
}

/// <summary>
/// Flat, serializable description of something Homeostat did, used by the built-in notification and audit
/// observers. <see cref="Summary"/> is a ready-to-read sentence.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Timestamp">When it happened.</param>
/// <param name="Application">Application name (<see cref="HomeostatOptions.ApplicationName"/>).</param>
/// <param name="Condition">Condition concerned.</param>
/// <param name="Summary">One human-readable sentence.</param>
public sealed record HomeostatEvent(
    HomeostatEventKind Kind,
    DateTimeOffset Timestamp,
    string Application,
    string Condition,
    string Summary)
{
    /// <summary>Step name, for step and approval events.</summary>
    public string? Step { get; init; }

    /// <summary>
    /// Run outcome, step verification outcome, or approval status, depending on <see cref="Kind"/>.
    /// </summary>
    public string? Outcome { get; init; }

    /// <summary>Error message of a failed step.</summary>
    public string? Error { get; init; }

    /// <summary>Approval request identifier, for approval events.</summary>
    public string? ApprovalId { get; init; }

    /// <summary>When an approval request expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Who approved or rejected a request.</summary>
    public string? By { get; init; }
}
