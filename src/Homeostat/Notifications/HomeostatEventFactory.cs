using Homeostat.Conditions;
using Homeostat.Healing;

namespace Homeostat.Notifications;

/// <summary>Turns observer callbacks into <see cref="HomeostatEvent"/> instances.</summary>
internal sealed class HomeostatEventFactory(string application, TimeProvider timeProvider)
{
    public HomeostatEvent Activated(ConditionSnapshot c) => new()
    {
        Kind = HomeostatEventKind.ConditionActivated,
        Timestamp = c.ActiveSince ?? Now,
        Application = application,
        Condition = c.Name,
        Group = c.Group,
        Summary = c.SuppressedBy is { } cause
            ? $"Condition '{c.Name}' is active (suppressed by '{cause}')."
            : $"Condition '{c.Name}' is active.",
    };

    public HomeostatEvent Cleared(ConditionSnapshot c) => new()
    {
        Kind = HomeostatEventKind.ConditionCleared,
        Timestamp = c.LastSignalAt ?? Now,
        Application = application,
        Condition = c.Name,
        Group = c.Group,
        Summary = $"Condition '{c.Name}' cleared.",
    };

    public HomeostatEvent Step(HealingStepResult s) => new()
    {
        Kind = HomeostatEventKind.StepCompleted,
        Timestamp = Now,
        Application = application,
        Condition = s.ConditionName,
        Group = s.Group,
        Summary = !s.Succeeded
            ? $"Step '{s.Name}' of '{s.ConditionName}' failed: {s.Error?.Message}"
            : $"Step '{s.Name}' of '{s.ConditionName}' completed; verification: {s.Verification}.",
        RunId = s.RunId,
        Step = s.Name,
        Outcome = s.Verification?.ToString(),
        Error = s.Error?.Message,
    };

    public HomeostatEvent Run(HealingRunResult r) => new()
    {
        Kind = HomeostatEventKind.RunCompleted,
        Timestamp = r.CompletedAt,
        Application = application,
        Condition = r.ConditionName,
        Group = r.Group,
        Summary = $"Healing of '{r.ConditionName}' ended: {r.Outcome} after {r.Steps.Count} step(s).",
        RunId = r.RunId,
        Instance = r.Instance,
        Outcome = r.Outcome.ToString(),
        Step = r.Steps.Count > 0 ? r.Steps[^1].Name : null,
    };

    public HomeostatEvent ApprovalRequested(PendingApproval a) => new()
    {
        Kind = HomeostatEventKind.ApprovalRequested,
        Timestamp = a.RequestedAt,
        Application = application,
        Condition = a.ConditionName,
        Group = a.Group,
        Summary = $"Approval needed for step '{a.StepName}' of '{a.ConditionName}' (request {a.Id}, expires {a.ExpiresAt:u}).",
        Step = a.StepName,
        ApprovalId = a.Id,
        ExpiresAt = a.ExpiresAt,
    };

    public HomeostatEvent ApprovalResolved(ApprovalResolution r)
    {
        var who = r.ResolvedBy is { } by ? $" by {by}" : string.Empty;
        return new()
        {
            Kind = HomeostatEventKind.ApprovalResolved,
            Timestamp = r.ResolvedAt,
            Application = application,
            Condition = r.Request.ConditionName,
            Group = r.Request.Group,
            Summary = r.Status switch
            {
                ApprovalStatus.Approved => $"Step '{r.Request.StepName}' of '{r.Request.ConditionName}' approved{who}.",
                ApprovalStatus.Rejected => $"Step '{r.Request.StepName}' of '{r.Request.ConditionName}' rejected{who}.",
                ApprovalStatus.Expired => $"Approval for step '{r.Request.StepName}' of '{r.Request.ConditionName}' expired.",
                _ => $"Approval for step '{r.Request.StepName}' of '{r.Request.ConditionName}' withdrawn (condition cleared or application stopping).",
            },
            Step = r.Request.StepName,
            ApprovalId = r.Request.Id,
            Outcome = r.Status.ToString(),
            By = r.ResolvedBy,
        };
    }

    private DateTimeOffset Now => timeProvider.GetUtcNow();
}
