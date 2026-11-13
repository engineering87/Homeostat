using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Samples.Architectures.ForecastApi;

/// <summary>
/// What the team knows about the forecast API, written down for Homeostat. The reasoning behind each line is in
/// docs/architecture-examples.md.
/// </summary>
public static class ForecastApiHealing
{
    /// <summary>Skip the shared cache on this instance.</summary>
    public const string BypassCache = "bypass-forecast-cache";

    /// <summary>Answer with the last cached forecast, marked as stale, when the primary provider fails.</summary>
    public const string ServeStaleForecasts = "serve-stale-forecasts";

    /// <summary>Send every forecast request to the backup provider.</summary>
    public const string UseBackupProvider = "use-backup-forecast-provider";

    /// <summary>Show coordinates instead of place names.</summary>
    public const string HidePlaceNames = "hide-place-names";

    /// <summary>Adds the probes, conditions and plans of the forecast API.</summary>
    public static HomeostatBuilder AddForecastApiHealing(this HomeostatBuilder h) => h
        // Observe the primary provider directly: after a failover no user traffic reaches it, and only a probe can
        // tell when it is back.
        .AddLatencyProbe("forecast-primary",
            (sp, ct) => sp.GetRequiredService<IForecastProviders>().PingPrimaryAsync(ct),
            p =>
            {
                p.Interval = TimeSpan.FromSeconds(15);
                p.UnhealthyLatency = TimeSpan.FromSeconds(2);
            })
        .AddLatencyProbe("forecast-cache",
            (sp, ct) => sp.GetRequiredService<IForecastCache>().PingAsync(ct),
            p =>
            {
                p.Interval = TimeSpan.FromSeconds(10);
                p.UnhealthyLatency = TimeSpan.FromMilliseconds(500);
            })

        // Each instance has its own connection to the cache: each one decides for itself.
        .AddCondition("forecast-cache-down", c => c
            .ForSource("forecast-cache")
            .SustainedFor(TimeSpan.FromSeconds(30))
            .ClearAfter(TimeSpan.FromMinutes(1))
            .Heal(p => p.TurnOnSwitch(BypassCache)))

        // The provider is shared by every instance: one decision for the whole deployment.
        .AddCondition("forecast-primary-down", c => c
            .ForSource("forecast-primary")
            .SustainedFor(TimeSpan.FromMinutes(2))
            // The primary must answer for 5 minutes before traffic goes back to it.
            .ClearAfter(TimeSpan.FromMinutes(5))
            // With the cache down every request reaches the provider: its slowness is our own load, and failing over
            // would only move that load to the backup.
            .SuppressedBy("forecast-cache-down")
            .Heal(p => p
                // Least invasive first: users get yesterday's data instead of an error.
                .TurnOnSwitch(ServeStaleForecasts)
                // The backup provider has a different model and a cost per call: a person agrees first.
                .TurnOnSwitch(UseBackupProvider).RequireApproval(TimeSpan.FromMinutes(15))
                .OncePerCluster()))

        // Place names are a convenience: hide them rather than fail the forecast.
        .AddCondition("geocoder-down", c => c
            .ForSource("http:geocoder")
            .SustainedFor(TimeSpan.FromMinutes(1))
            .Heal(p => p
                .TurnOnSwitch(HidePlaceNames)
                // With the names hidden the geocoder gets no calls, so its signal stops and the condition cannot
                // clear by itself: try the geocoder again after 10 minutes.
                .HoldAtMost(TimeSpan.FromMinutes(10))));
}
