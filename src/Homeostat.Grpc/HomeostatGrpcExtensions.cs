using Grpc.Net.ClientFactory;
using Homeostat.Calls;
using Homeostat.Grpc;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat;

/// <summary>Homeostat signals for clients of the gRPC client factory.</summary>
public static class HomeostatGrpcExtensions
{
    /// <summary>
    /// Publishes, at every detection interval, the failure ratio and 95th percentile duration of the calls of this gRPC
    /// client, as the signal <c>grpc:{client name}</c>. Calls are judged on their gRPC status, so failures that gRPC
    /// carries inside HTTP 200 responses, including those reported at the end of a stream, count. Requires
    /// <c>AddHomeostat</c>.
    /// </summary>
    /// <param name="builder">The builder returned by <c>AddGrpcClient</c>.</param>
    /// <param name="configure">Source, failure statuses and thresholds; optional.</param>
    /// <returns>The builder.</returns>
    /// <remarks>
    /// Use it instead of the HTTP-level <c>AddHomeostatSignals</c> of Homeostat.Http on the same client: that one sees
    /// only the HTTP exchange and misses statuses sent in trailers. Calling it again on the same client replaces the
    /// settings without adding a second interceptor. See <see cref="HomeostatGrpcInterceptor"/> for which calls are
    /// recorded and how.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddGrpcClient&lt;Forecasts.ForecastsClient&gt;(o => o.Address = new Uri("https://forecasts:5001"))
    ///     .AddHomeostatGrpcSignals(o => o.UnhealthyLatency = TimeSpan.FromSeconds(2));
    ///
    /// builder.Services.AddHomeostat(h => h.AddCondition("forecasts-failing", c => c
    ///     .ForSource("grpc:ForecastsClient")
    ///     .SustainedFor(TimeSpan.FromMinutes(1))
    ///     .Heal(p => p.TurnOnSwitch("serve-cached-forecast", turnOffWhenCleared: true))));
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddHomeostatGrpcSignals(this IHttpClientBuilder builder, Action<GrpcSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new GrpcSignalOptions();
        configure?.Invoke(options);
        ArgumentNullException.ThrowIfNull(options.IsFailure, nameof(GrpcSignalOptions.IsFailure));

        var clientName = builder.Name ?? throw new InvalidOperationException(
            "AddHomeostatGrpcSignals must be called on a named or typed gRPC client.");
        var source = string.IsNullOrWhiteSpace(options.Source) ? $"grpc:{clientName}" : options.Source;
        builder.Services.AddCallSignals(source, "grpc-client", t =>
        {
            t.DegradedFailureRatio = options.DegradedFailureRatio;
            t.UnhealthyFailureRatio = options.UnhealthyFailureRatio;
            t.DegradedLatency = options.DegradedLatency;
            t.UnhealthyLatency = options.UnhealthyLatency;
            t.MinimumCalls = options.MinimumCalls;
        });

        // Calling this twice on the same client updates the settings but must not add a second interceptor, otherwise
        // every call would be counted twice.
        var existing = builder.Services
            .Where(d => d.ServiceType == typeof(GrpcClientSignalRegistration))
            .Select(d => d.ImplementationInstance)
            .OfType<GrpcClientSignalRegistration>()
            .FirstOrDefault(r => r.ClientName == clientName);
        if (existing is not null)
        {
            existing.Source = source;
            existing.IsFailure = options.IsFailure;
            return builder;
        }

        var registration = new GrpcClientSignalRegistration(clientName) { Source = source, IsFailure = options.IsFailure };
        builder.Services.AddSingleton(registration);
        return builder.AddInterceptor(sp => new HomeostatGrpcInterceptor(
            sp.GetRequiredKeyedService<CallRecorder>(registration.Source), registration.IsFailure, sp.GetService<TimeProvider>()));
    }
}

/// <summary>The gRPC clients already instrumented, so a second call does not add a second interceptor.</summary>
internal sealed class GrpcClientSignalRegistration(string clientName)
{
    public string ClientName { get; } = clientName;

    public required string Source { get; set; }

    public required Func<global::Grpc.Core.StatusCode, bool> IsFailure { get; set; }
}
