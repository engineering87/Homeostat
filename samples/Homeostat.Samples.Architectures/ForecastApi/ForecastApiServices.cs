using Homeostat.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Samples.Architectures.ForecastApi;

/// <summary>The two forecast providers (your code): Homeostat only needs a cheap call that tells whether the primary answers.</summary>
public interface IForecastProviders
{
    /// <summary>Calls the primary provider's status endpoint; throws when it does not answer.</summary>
    Task PingPrimaryAsync(CancellationToken cancellationToken);
}

/// <summary>The shared forecast cache (your code).</summary>
public interface IForecastCache
{
    /// <summary>Round trip to the cache; throws when it cannot be reached.</summary>
    Task PingAsync(CancellationToken cancellationToken);
}

/// <summary>Registers the HTTP clients of the forecast API, with the signals Homeostat watches.</summary>
public static class ForecastApiServices
{
    /// <summary>The geocoder client publishes <c>http:geocoder</c>: failure ratio and p95 latency of its calls.</summary>
    public static IServiceCollection AddForecastApiClients(this IServiceCollection services)
    {
        services.AddHttpClient("geocoder", c => c.BaseAddress = new Uri("https://geocoder.example"))
            .AddHomeostatSignals(o => o.UnhealthyLatency = TimeSpan.FromSeconds(3));
        return services;
    }
}

/// <summary>
/// How the application reads the switches the plans turn on (your code). The order of the checks is part of the
/// knowledge: the backup provider wins over stale data, and stale data wins over an error.
/// </summary>
public sealed class ForecastSource(IHomeostatSwitches switches)
{
    /// <summary>Where the next forecast comes from.</summary>
    public string Choose(bool primaryCallFailed)
    {
        if (switches.IsOn(ForecastApiHealing.UseBackupProvider))
        {
            return "backup provider";
        }

        if (primaryCallFailed && switches.IsOn(ForecastApiHealing.ServeStaleForecasts))
        {
            return "last forecast in the cache, marked as stale";
        }

        return "primary provider";
    }

    /// <summary>Whether to look up place names, or show coordinates only.</summary>
    public bool ShowPlaceNames => !switches.IsOn(ForecastApiHealing.HidePlaceNames);

    /// <summary>Whether to read and write the shared cache, or skip it.</summary>
    public bool UseCache => !switches.IsOn(ForecastApiHealing.BypassCache);
}
