using Homeostat.Conditions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homeostat.HealthChecks;

/// <summary>Reports whether Homeostat needs attention.</summary>
internal sealed class HomeostatHealthCheck : IHealthCheck
{
    /// <summary>Marks Homeostat's own check so it is not published as a signal.</summary>
    public const string Tag = "homeostat:self";

    private readonly IHomeostatControl _control;
    private readonly IHomeostatApprovals _approvals;
    // For both store reads together: well under the one second Kubernetes gives a probe by default.
    internal static readonly TimeSpan StoreTimeout = TimeSpan.FromMilliseconds(800);

    private readonly HealthStatus _attentionStatus;
    private readonly ILogger _logger;

    public HomeostatHealthCheck(IHomeostatControl control, IHomeostatApprovals approvals, HealthStatus attentionStatus, ILogger? logger = null)
    {
        _control = control;
        _approvals = approvals;
        _attentionStatus = attentionStatus;
        _logger = logger ?? NullLogger.Instance;
    }

    // Per Homeostat instance (the check itself is created for every probe).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IHomeostatControl, System.Runtime.CompilerServices.StrongBox<Task<StoreReads>?>> s_reads = new();

    // When the store failure was last logged as a warning (Environment.TickCount64), per Homeostat instance.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IHomeostatControl, System.Runtime.CompilerServices.StrongBox<long>> s_lastStoreLog = new();

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var active = _control.GetConditions().Where(c => c.Status == ConditionStatus.Active).Select(c => c.Name).ToArray();
        Guardrails.ActionBudgetStatus budget;
        int pending;
        IReadOnlyList<Guardrails.ActionBudgetStatus> groupBudgets;
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
                // Concurrent probes: only the first one's read is started, the others share it. The slot is taken with
                // a completion source first, so a probe that loses the race starts nothing.
                var read = new TaskCompletionSource<StoreReads>(TaskCreationOptions.RunContinuationsAsynchronously);
                var current = Interlocked.CompareExchange(ref box.Value, read.Task, reads);
                if (current == reads)
                {
                    _ = ReadIntoAsync(read);
                    reads = read.Task;
                }
                else
                {
                    reads = current!;
                }
            }

            (budget, pending, groupBudgets) = await reads.WaitAsync(StoreTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Degraded whatever the attention status: the store is shared, so an outage fails this check on every
            // replica at once, and Unhealthy would take them all out of rotation for a problem of Homeostat, not of
            // the application (which keeps serving; Homeostat fails closed). Degraded answers 200 by default.
            // The exception goes to the log only, not into the result: a store's messages can name servers, and a
            // health endpoint, often anonymous, may print the result's exception (decision I7).
            // Warning at most once a minute per Homeostat instance, Debug otherwise: probes can come every second.
            var lastLog = s_lastStoreLog.GetValue(_control, static _ => new(long.MinValue / 2));
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref lastLog.Value);
            var level = now - last >= 60_000 && Interlocked.CompareExchange(ref lastLog.Value, now, last) == last
                ? LogLevel.Warning
                : LogLevel.Debug;
            _logger.Log(level, ex, "The Homeostat health check could not read the store.");
            return HealthCheckResult.Degraded("The Homeostat store is unavailable.");
        }

        var data = new Dictionary<string, object>
        {
            ["paused"] = _control.IsPaused,
            ["dry_run"] = _control.IsDryRun,
            ["active_conditions"] = active,
            ["budget_remaining"] = budget.Remaining,
            ["pending_approvals"] = pending,
        };

        // Healing groups: listed only when the application has some, so the data of an application without groups is
        // unchanged. A paused group, like the global pause, needs attention (its healing stopped); so does an exhausted
        // group budget, like the global budget (the group's healing stopped while the others go on).
        var groups = _control.GetGroups();
        var pausedGroups = groups.Where(g => g.IsPaused).ToArray();
        var exhaustedGroups = groupBudgets.Where(b => b.IsExhausted && b.Group is not null).Select(b => b.Group!).ToArray();
        if (groups.Count > 0)
        {
            data["paused_groups"] = pausedGroups.Select(g => g.Name).ToArray();
            data["exhausted_group_budgets"] = exhaustedGroups;
        }

        var reasons = new List<string>();
        if (_control.PauseReason is { } pause)
        {
            reasons.Add(pause);
        }

        if (pausedGroups.Length > 0)
        {
            reasons.Add($"paused groups: {string.Join(", ", pausedGroups.Select(g => g.PauseReason is { } why ? $"{g.Name} ({why})" : g.Name))}");
        }

        if (budget.IsExhausted)
        {
            reasons.Add("action budget exhausted");
        }

        foreach (var group in exhaustedGroups)
        {
            reasons.Add($"action budget of group '{group}' exhausted");
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

    // Off the request thread (a store may block before returning its task); never throws: the outcome goes to the probes.
    private async Task ReadIntoAsync(TaskCompletionSource<StoreReads> read)
    {
        try
        {
            read.TrySetResult(await Task.Run(ReadStoreAsync, CancellationToken.None).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            read.TrySetException(ex);
        }
    }

    // Every read together, inside the one shared, bounded read: the global budget, the pending approvals and the budget
    // of each group that has one (the configured groups, a set fixed by the application). One that fails fails them all:
    // the store is then reported as unavailable (decision I6), never a group budget as not exhausted.
    private async Task<StoreReads> ReadStoreAsync()
    {
        var budgetRead = _control.GetBudgetAsync(CancellationToken.None).AsTask();
        var pendingRead = _approvals.GetPendingAsync(CancellationToken.None).AsTask();
        var groupReads = _control.GetGroups()
            .Where(g => g.MaxActionsPerWindow is not null)
            .Select(g => _control.GetGroupBudgetAsync(g.Name, CancellationToken.None).AsTask())
            .ToArray();
        var groupsRead = Task.WhenAll(groupReads);

        // Every read observed, even when one fails first.
        await Task.WhenAll(budgetRead, pendingRead, groupsRead).ConfigureAwait(false);
        return new StoreReads(budgetRead.Result, pendingRead.Result.Count, groupsRead.Result.OfType<Guardrails.ActionBudgetStatus>().ToArray());
    }

    private sealed record StoreReads(Guardrails.ActionBudgetStatus Budget, int Pending, IReadOnlyList<Guardrails.ActionBudgetStatus> GroupBudgets);
}
