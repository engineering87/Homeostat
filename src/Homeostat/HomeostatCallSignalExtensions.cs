using Homeostat.Calls;
using Homeostat.Detectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Homeostat;

/// <summary>Signals from the outcome of the calls to any dependency.</summary>
public static class HomeostatCallSignalExtensions
{
    /// <summary>
    /// Registers a <see cref="CallRecorder"/> for <paramref name="source"/> (a keyed singleton) and publishes, at every
    /// detection interval, the failure ratio and 95th percentile duration of the calls it recorded. The client code
    /// resolves the recorder with <c>GetRequiredKeyedService&lt;CallRecorder&gt;(source)</c> and calls
    /// <c>Record</c> or <c>TrackAsync</c>.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="source">Signal source, by convention <c>{technology}:{name}</c>, for example <c>"geocoding:api"</c>.</param>
    /// <param name="configure">Thresholds; optional.</param>
    /// <returns>The builder.</returns>
    /// <example>
    /// <code>
    /// h.AddCallSignals("geocoding:api", o => o.UnhealthyLatency = TimeSpan.FromSeconds(2))
    ///  .AddCondition("geocoding-failing", c => c.ForSource("geocoding:api").SustainedFor(TimeSpan.FromMinutes(1))
    ///      .Heal(p => p.TurnOnSwitch("use-cached-coordinates", turnOffWhenCleared: true)));
    ///
    /// public sealed class Geocoder(GeocodingSdk sdk, [FromKeyedServices("geocoding:api")] CallRecorder calls)
    /// {
    ///     public Task&lt;Coordinates&gt; LookupAsync(string address, CancellationToken ct)
    ///         => calls.TrackAsync(token => sdk.GeocodeAsync(address, token), ct);
    /// }
    /// </code>
    /// </example>
    public static HomeostatBuilder AddCallSignals(this HomeostatBuilder builder, string source, Action<CallSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddCallSignals(source, "calls", configure);
        return builder;
    }

    /// <summary>
    /// Same as <see cref="AddCallSignals(HomeostatBuilder, string, Action{CallSignalOptions}?)"/>, for integration
    /// packages that register call signals where only the service collection is available.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="source">Signal source.</param>
    /// <param name="origin">Value of the <c>origin</c> tag of the signals, naming the integration (for example <c>"grpc-client"</c>).</param>
    /// <param name="configure">Thresholds; optional.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddCallSignals(this IServiceCollection services, string source, string origin, Action<CallSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);
        var options = new CallSignalOptions();
        configure?.Invoke(options);
        options.Validate(source);

        services.AddKeyedSingleton(source, (sp, _) => new CallRecorder(source, options, sp.GetService<TimeProvider>()));
        services.AddSingleton(new CallSignalRegistration(source, origin));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSignalDetector<CallSignalDetector>();
        return services;
    }
}
