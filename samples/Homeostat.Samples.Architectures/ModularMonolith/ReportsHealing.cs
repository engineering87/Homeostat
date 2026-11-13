using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Samples.Architectures.ModularMonolith;

/// <summary>
/// What the team of the reports module knows about it, written down for Homeostat. The reasoning behind each line is
/// in docs/architecture-examples.md.
/// </summary>
public static class ReportsHealing
{
    /// <summary>The healing group of the reports module.</summary>
    public const string Group = "reports";

    /// <summary>Accept no new export; the running ones finish.</summary>
    public const string Paused = "reports-paused";

    /// <summary>Adds the maintenance window, the probes, the conditions and the plans of the reports module.</summary>
    public static HomeostatBuilder AddReportsHealing(this HomeostatBuilder h) => h
        // A module whose healing does not help spends its own 2 actions per hour, never those of the others; its runs
        // can last minutes, so they hold at most one of the 3 slots.
        .ConfigureGroup(Group, g =>
        {
            g.MaxActionsPerWindow = 2;
            g.MaxConcurrentHealings = 1;
        })
        // The reports database is reindexed every Sunday from 02:00 to 04:00 UTC, and is slow on purpose then.
        .AddMaintenanceWindow("reports-reindex", w => w
            .Daily(new TimeOnly(2, 0), TimeSpan.FromHours(2))
            .On(DayOfWeek.Sunday)
            .ForGroups(Group))
        .AddLatencyProbe("reports-db",
            (sp, ct) => sp.GetRequiredService<IReportsDatabase>().PingAsync(ct),
            p =>
            {
                p.Interval = TimeSpan.FromSeconds(15);
                p.DegradedLatency = TimeSpan.FromMilliseconds(500);
                p.UnhealthyLatency = TimeSpan.FromSeconds(3);
            })
        .AddLatencyProbe("reports-forecast-reads",
            (sp, ct) => sp.GetRequiredService<IReportsForecastReader>().PingAsync(ct),
            p =>
            {
                p.Interval = TimeSpan.FromSeconds(10);
                p.UnhealthyLatency = TimeSpan.FromSeconds(1);
            })

        // Stale statistics make the export queries slow; refreshing them takes up to 20 minutes.
        .AddCondition("reports-db-slow", c => c
            .ForSource("reports-db")
            .InGroup(Group)
            .WhenSeverityAtLeast(SignalSeverity.Degraded)
            .SustainedFor(TimeSpan.FromMinutes(5))
            .ClearAfter(TimeSpan.FromMinutes(10))
            .Heal(p => p
                .With("refresh-reports-statistics",
                    (context, ct) => context.Services.GetRequiredService<IReportsDatabase>().RefreshStatisticsAsync(ct))
                .ActionTimeout(TimeSpan.FromMinutes(20))))

        // The module reads the forecasts store through its own connections. When the store itself is down, new
        // connections do not help: the forecasts module heals the cause.
        .AddCondition("reports-forecast-reads-failing", c => c
            .ForSource("reports-forecast-reads")
            .InGroup(Group)
            .SustainedFor(TimeSpan.FromMinutes(1))
            .ClearAfter(TimeSpan.FromMinutes(2))
            .SuppressedBy("forecast-store-down")
            .Heal(p => p
                .With("reset-reports-forecast-connections",
                    (context, ct) => context.Services.GetRequiredService<IReportsForecastReader>().ResetConnectionsAsync(ct))
                // A reset is cheap: try again after 2 minutes, within the group's budget.
                .Cooldown(TimeSpan.FromMinutes(2))));
}
