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
    private static readonly TimeSpan StoreTimeout = TimeSpan.FromSeconds(2);

    private readonly HealthStatus _attentionStatus;

    public HomeostatHealthCheck(IHomeostatControl control, IHomeostatApprovals approvals, HealthStatus attentionStatus)
    {
        _control = control;
        _approvals = approvals;
        _attentionStatus = attentionStatus;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var active = _control.GetConditions().Where(c => c.Status == ConditionStatus.Active).Select(c => c.Name).ToArray();
        Guardrails.ActionBudgetStatus budget;
        int pending;
        try
        {
            // Bounded: a store that does not answer must not make the health endpoint time out (a liveness probe that
            // times out restarts the application).
            budget = await _control.GetBudgetAsync(cancellationToken).AsTask().WaitAsync(StoreTimeout, cancellationToken).ConfigureAwait(false);
            pending = (await _approvals.GetPendingAsync(cancellationToken).AsTask().WaitAsync(StoreTimeout, cancellationToken).ConfigureAwait(false)).Count;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(_attentionStatus, "The Homeostat store is unavailable.", ex);
        }


        var data = new Dictionary<string, object>
        {
            ["paused"] = _control.IsPaused,
            ["dry_run"] = _control.IsDryRun,
            ["active_conditions"] = active,
            ["budget_remaining"] = budget.Remaining,
            ["pending_approvals"] = pending,
        };

        var reasons = new List<string>();
        if (_control.PauseReason is { } pause)
        {
            reasons.Add(pause);
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
        return result;
    }
}
