using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Samples.Architectures.ModularMonolith;

/// <summary>
/// What the team of the forecasts module knows about it, written down for Homeostat. The reasoning behind each line is
/// in docs/architecture-examples.md.
/// </summary>
public static class ForecastsHealing
{
    /// <summary>The healing group of the forecasts module.</summary>
    public const string Group = "forecasts";

    /// <summary>Serve the last forecasts in the store instead of asking the provider.</summary>
    public const string ServeLastForecasts = "serve-last-forecasts";

    /// <summary>Adds the probes, conditions and plans of the forecasts module.</summary>
    public static HomeostatBuilder AddForecastsHealing(this HomeostatBuilder h) => h
        .ConfigureGroup(Group, g => g.MaxActionsPerWindow = 3)
        .AddLatencyProbe("forecast-provider",
            (sp, ct) => sp.GetRequiredService<IForecastProvider>().PingAsync(ct),
            p =>
            {
                p.Interval = TimeSpan.FromSeconds(15);
                p.UnhealthyLatency = TimeSpan.FromSeconds(2);
            })
        .AddLatencyProbe("forecast-store",
            (sp, ct) => sp.GetRequiredService<IForecastStore>().PingAsync(ct),
            p =>
            {
                p.Interval = TimeSpan.FromSeconds(10);
                p.UnhealthyLatency = TimeSpan.FromSeconds(1);
            })

        // The store holds the forecasts every module reads: when it is down, the read errors of the other modules
        // are its symptoms.
        .AddCondition("forecast-store-down", c => c
            .ForSource("forecast-store")
            .InGroup(Group)
            .SustainedFor(TimeSpan.FromSeconds(30))
            .ClearAfter(TimeSpan.FromMinutes(2))
            .Heal(p => p.With("reopen-forecast-store",
                (context, ct) => context.Services.GetRequiredService<IForecastStore>().ReopenAsync(ct))))

        // The last forecasts are better than an error.
        .AddCondition("forecast-provider-down", c => c
            .ForSource("forecast-provider")
            .InGroup(Group)
            .SustainedFor(TimeSpan.FromMinutes(1))
            .ClearAfter(TimeSpan.FromMinutes(5))
            .Heal(p => p.TurnOnSwitch(ServeLastForecasts)));
}
