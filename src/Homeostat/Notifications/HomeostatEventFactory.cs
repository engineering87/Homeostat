using Homeostat.Conditions;
using Homeostat.Healing;

namespace Homeostat.Notifications;

/// <summary>Turns observer callbacks into <see cref="HomeostatEvent"/> instances.</summary>
internal sealed class HomeostatEventFactory(string application, TimeProvider timeProvider)
{
    public HomeostatEvent Activated(ConditionSnapshot c)
        => new(HomeostatEventKind.ConditionActivated, c.ActiveSince ?? Now, application, c.Name,
            c.SuppressedBy is { } cause
                ? $"Condition '{c.Name}' is active (suppressed by '{cause}')."
                : $"Condition '{c.Name}' is active.");

    public HomeostatEvent Cleared(ConditionSnapshot c)
        => new(HomeostatEventKind.ConditionCleared, c.LastSignalAt ?? Now, application, c.Name, $"Condition '{c.Name}' cleared.");

    public HomeostatEvent Step(HealingStepResult s)
    {
        var summary = !s.Succeeded
            ? $"Step '{s.Name}' of '{s.ConditionName}' failed: {s.Error?.Message}"
            : $"Step '{s.Name}' of '{s.ConditionName}' completed; verification: {s.Verification}.";
        return new(HomeostatEventKind.StepCompleted, Now, application, s.ConditionName, summary)
        {
            Step = s.Name,
            Outcome = s.Verification?.ToString(),
            Error = s.Error?.Message,
        };
    }

    public HomeostatEvent Run(HealingRunResult r)
        => new(HomeostatEventKind.RunCompleted, r.CompletedAt, application, r.ConditionName,
            $"Healing of '{r.ConditionName}' ended: {r.Outcome} after {r.Steps.Count} step(s).")
        {
            Outcome = r.Outcome.ToString(),
            Step = r.Steps.Count > 0 ? r.Steps[^1].Name : null,
        };

    public HomeostatEvent ApprovalRequested(PendingApproval a)
        => new(HomeostatEventKind.ApprovalRequested, a.RequestedAt, application, a.ConditionName,
            $"Approval needed for step '{a.StepName}' of '{a.ConditionName}' (request {a.Id}, expires {a.ExpiresAt:u}).")
        {
            Step = a.StepName,
            ApprovalId = a.Id,
            ExpiresAt = a.ExpiresAt,
        };

    public HomeostatEvent ApprovalResolved(ApprovalResolution r)
    {
        var who = r.ResolvedBy is { } by ? $" by {by}" : string.Empty;
        var summary = r.Status switch
        {
            ApprovalStatus.Approved => $"Step '{r.Request.StepName}' of '{r.Request.ConditionName}' approved{who}.",
            ApprovalStatus.Rejected => $"Step '{r.Request.StepName}' of '{r.Request.ConditionName}' rejected{who}.",
            ApprovalStatus.Expired => $"Approval for step '{r.Request.StepName}' of '{r.Request.ConditionName}' expired.",
            _ => $"Approval for step '{r.Request.StepName}' of '{r.Request.ConditionName}' withdrawn (condition cleared or application stopping).",
        };
        return new(HomeostatEventKind.ApprovalResolved, r.ResolvedAt, application, r.Request.ConditionName, summary)
        {
            Step = r.Request.StepName,
            ApprovalId = r.Request.Id,
            Outcome = r.Status.ToString(),
            By = r.ResolvedBy,
        };
    }

    private DateTimeOffset Now => timeProvider.GetUtcNow();
}
