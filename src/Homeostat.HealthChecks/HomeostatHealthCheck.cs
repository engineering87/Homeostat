using Homeostat.Conditions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Homeostat.HealthChecks;

/// <summary>Reports whether Homeostat needs attention.</summary>
internal sealed class HomeostatHealthCheck : IHealthCheck
{
    /// <summary>Marks Homeostat's own check so it is not published as a signal.</summary>
    public const string Tag = "homeostat:self";

    private readonly IHomeostatControl _control;
    private readonly IHomeostatApprovals _approvals;
    private readonly HealthStatus _attentionStatus;

    public HomeostatHealthCheck(IHomeostatControl control, IHomeostatApprovals approvals, HealthStatus attentionStatus)
    {
        _control = control;
        _approvals = approvals;
        _attentionStatus = attentionStatus;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var active = _control.GetConditions().Where(c => c.Status == ConditionStatus.Active).Select(c => c.Name).ToArray();
        var budget = _control.GetBudget();
        var pending = _approvals.GetPending().Count;

        var data = new Dictionary<string, object>
        {
            ["paused"] = _control.IsPaused,
            ["dry_run"] = _control.IsDryRun,
            ["active_conditions"] = active,
            ["budget_remaining"] = budget.Remaining,
            ["pending_approvals"] = pending,
        };

        var reasons = new List<string>();
        if (_control.IsPaused)
        {
            reasons.Add("paused");
        }

        if (budget.IsExhausted)
        {
            reasons.Add("action budget exhausted");
        }

        if (pending > 0)
        {
            reasons.Add($"{pending} step(s) waiting for approval");
        }

        if (active.Length > 0)
        {
            reasons.Add($"active: {string.Join(", ", active)}");
        }

        var result = reasons.Count == 0
            ? HealthCheckResult.Healthy("No active condition.", data)
            : new HealthCheckResult(_attentionStatus, string.Join("; ", reasons), data: data);
        return Task.FromResult(result);
    }
}
