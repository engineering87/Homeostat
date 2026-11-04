using Homeostat.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Homeostat;

/// <summary>Instruments <c>IHttpClientFactory</c> clients.</summary>
public static class HomeostatHttpClientBuilderExtensions
{
    /// <summary>
    /// Publishes a signal for this client at every detection interval, based on its failure ratio and,
    /// optionally, its 95th percentile latency. The source is <c>"http:{client name}"</c> unless
    /// <see cref="HttpClientSignalOptions.Source"/> is set. Requires <c>AddHomeostat</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddHttpClient("forecast", c => c.BaseAddress = new Uri("https://forecast.example"))
    ///     .AddHomeostatSignals(o => o.UnhealthyLatency = TimeSpan.FromSeconds(2));
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddHomeostatSignals(this IHttpClientBuilder builder, Action<HttpClientSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new HttpClientSignalOptions();
        configure?.Invoke(options);

        var clientName = builder.Name ?? throw new InvalidOperationException(
            "AddHomeostatSignals must be called on a named or typed client, not inside ConfigureHttpClientDefaults.");

        // Calling this twice on the same client replaces the options but must not add a second handler,
        // otherwise every request would be counted twice.
        var alreadyInstrumented = builder.Services.Any(d =>
            d.ServiceType == typeof(HttpClientSignalRegistration)
            && d.ImplementationInstance is HttpClientSignalRegistration existing
            && existing.ClientName == clientName);

        builder.Services.AddSingleton(new HttpClientSignalRegistration(clientName, options));
        builder.Services.TryAddSingleton<HttpClientSignalRegistry>();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddHomeostatDetector<HttpClientSignalDetector>();

        if (!alreadyInstrumented)
        {
            builder.AddHttpMessageHandler(sp => new HomeostatSignalHandler(
                sp.GetRequiredService<HttpClientSignalRegistry>().Get(clientName),
                sp.GetRequiredService<TimeProvider>()));
        }

        return builder;
    }
}
