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
    // For both store reads together: well under the one second Kubernetes gives a probe by default.
    private static readonly TimeSpan StoreTimeout = TimeSpan.FromMilliseconds(800);

    private readonly HealthStatus _attentionStatus;

    public HomeostatHealthCheck(IHomeostatControl control, IHomeostatApprovals approvals, HealthStatus attentionStatus)
    {
        _control = control;
        _approvals = approvals;
        _attentionStatus = attentionStatus;
    }

    // Per Homeostat instance (the check itself is created for every probe).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IHomeostatControl, System.Runtime.CompilerServices.StrongBox<Task<(Guardrails.ActionBudgetStatus Budget, int Pending)>?>> s_reads = new();

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var active = _control.GetConditions().Where(c => c.Status == ConditionStatus.Active).Select(c => c.Name).ToArray();
        Guardrails.ActionBudgetStatus budget;
        int pending;
        try
        {
            // Bounded, both reads together, and started off the request thread (a store may block before returning its
            // task): a store that does not answer must not make the health endpoint time out (a probe that times out
            // can restart the application).
            // One read at a time: while a read abandoned at the timeout still runs (a store blocked on I/O), probes do not
            // start another, so frequent probes cannot pile up blocked threads.
            var box = s_reads.GetValue(_control, static _ => new());
            var reads = Volatile.Read(ref box.Value);
            if (reads is null || reads.IsCompleted)
            {
                reads = Task.Run(async () =>
                {
                    var budgetRead = _control.GetBudgetAsync(CancellationToken.None).AsTask();
                    var pendingRead = _approvals.GetPendingAsync(CancellationToken.None).AsTask();
                    return (await budgetRead.ConfigureAwait(false), (await pendingRead.ConfigureAwait(false)).Count);
                }, CancellationToken.None);
                Volatile.Write(ref box.Value, reads);
            }

            (budget, pending) = await reads.WaitAsync(StoreTimeout, cancellationToken).ConfigureAwait(false);
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
