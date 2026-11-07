using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;

namespace Homeostat.Diagnostics;

/// <summary>
/// Observable gauges for the current state, on the <c>Homeostat</c> meter: conditions by status, pending approvals,
/// switches on, paused and dry-run. They live as long as the host, so several hosts in one process (tests) each
/// report their own state and stop reporting when they stop.
/// </summary>
internal sealed class HomeostatGauges : IHostedService, IDisposable
{
    private readonly IHomeostatControl _control;
    private readonly IHomeostatSwitches _switches;
    private readonly Engine.SharedControl _shared;
    private Meter? _meter;

    public HomeostatGauges(IHomeostatControl control, IHomeostatSwitches switches, Engine.SharedControl shared)
    {
        _control = control;
        _switches = switches;
        _shared = shared;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var meter = new Meter(HomeostatDiagnostics.Name);
        meter.CreateObservableGauge("homeostat.conditions", ConditionsByStatus,
            description: "Conditions by status (Inactive, Pending, Active).");
        meter.CreateObservableGauge("homeostat.approvals.pending", () => _shared.Current.Approvals.Count(a => a.Status is null),
            description: "Steps waiting for approval, on every instance (as last read from the store).");
        meter.CreateObservableGauge("homeostat.switches.on", () => _switches.GetActive().Count,
            description: "Switches currently on.");
        meter.CreateObservableGauge("homeostat.paused", () => _control.IsPaused ? 1 : 0,
            description: "1 while Homeostat is paused (kill switch, timed pause or maintenance window).");
        meter.CreateObservableGauge("homeostat.dry_run", () => _control.IsDryRun ? 1 : 0,
            description: "1 while Homeostat only logs actions.");
        _meter = meter;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => Interlocked.Exchange(ref _meter, null)?.Dispose();

    private IEnumerable<Measurement<int>> ConditionsByStatus()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["Inactive"] = 0, ["Pending"] = 0, ["Active"] = 0 };
        foreach (var condition in _control.GetConditions())
        {
            var status = condition.Status.ToString();
            counts[status] = counts.GetValueOrDefault(status) + 1;
        }

        return counts.Select(c => new Measurement<int>(c.Value, new KeyValuePair<string, object?>("status", c.Key)));
    }
}
