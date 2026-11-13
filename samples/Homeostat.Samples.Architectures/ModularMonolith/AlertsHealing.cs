using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Samples.Architectures.ModularMonolith;

/// <summary>
/// What the team of the alerts module knows about it, written down for Homeostat. The reasoning behind each line is in
/// docs/architecture-examples.md.
/// </summary>
public static class AlertsHealing
{
    /// <summary>The healing group of the alerts module.</summary>
    public const string Group = "alerts";

    /// <summary>Send alerts through the secondary gateway.</summary>
    public const string SecondaryGateway = "alerts-secondary-gateway";

    /// <summary>Evaluate the alert rules on the last forecasts the module holds in memory.</summary>
    public const string EvaluateOnSnapshot = "alerts-evaluate-on-snapshot";

    /// <summary>Adds the probes, conditions and plans of the alerts module.</summary>
    public static HomeostatBuilder AddAlertsHealing(this HomeostatBuilder h) => h
        // No budget of its own: the budgets of the other groups leave at least 3 of the 10 actions to alerts.
        .AddLatencyProbe("alerts-gateway",
            (sp, ct) => sp.GetRequiredService<IAlertGateway>().PingAsync(ct),
            p =>
            {
                p.Interval = TimeSpan.FromSeconds(10);
                p.UnhealthyLatency = TimeSpan.FromSeconds(2);
            })
        .AddLatencyProbe("alerts-forecast-reads",
            (sp, ct) => sp.GetRequiredService<IAlertsForecastReader>().PingAsync(ct),
            p =>
            {
                p.Interval = TimeSpan.FromSeconds(10);
                p.UnhealthyLatency = TimeSpan.FromSeconds(1);
            })

        .AddCondition("alerts-gateway-down", c => c
            .ForSource("alerts-gateway")
            .InGroup(Group)
            .SustainedFor(TimeSpan.FromSeconds(30))
            .ClearAfter(TimeSpan.FromMinutes(2))
            .Heal(p => p.TurnOnSwitch(SecondaryGateway)))

        // Not suppressed by forecast-store-down: whatever the cause, alerts must keep being evaluated, and the
        // snapshot in memory does not need the store.
        .AddCondition("alerts-forecast-reads-failing", c => c
            .ForSource("alerts-forecast-reads")
            .InGroup(Group)
            .SustainedFor(TimeSpan.FromSeconds(30))
            .ClearAfter(TimeSpan.FromMinutes(2))
            .Heal(p => p.TurnOnSwitch(EvaluateOnSnapshot)));
}
