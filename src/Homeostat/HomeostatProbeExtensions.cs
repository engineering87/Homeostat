using Homeostat.Probes;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat;

/// <summary>Registers probes: small functions that Homeostat calls periodically and turns into signals.</summary>
public static class HomeostatProbeExtensions
{
    /// <summary>
    /// Calls <paramref name="measure"/> periodically and publishes its result as the value of a signal from
    /// <paramref name="source"/>, classified with the thresholds in <see cref="ProbeOptions"/>. A probe that throws
    /// or times out publishes <see cref="ProbeOptions.FailureSeverity"/> without a value. Use it for numbers that no
    /// metric exposes yet: a queue or backlog depth, consumer lag, dead letters, free disk space.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="source">Source of the published signals; conditions select it with <c>ForSource</c>.</param>
    /// <param name="measure">Returns the current value. It runs in its own service scope and receives a token cancelled at the timeout.</param>
    /// <param name="configure">Changes interval, timeout and thresholds.</param>
    /// <example>
    /// <code>
    /// h.AddProbe("ingest-backlog", async (sp, ct) => await sp.GetRequiredService&lt;IngestQueue&gt;().CountAsync(ct),
    ///     p => { p.Interval = TimeSpan.FromSeconds(30); p.DegradedAbove = 1_000; p.UnhealthyAbove = 10_000; });
    /// </code>
    /// </example>
    public static HomeostatBuilder AddProbe(
        this HomeostatBuilder builder,
        string source,
        Func<IServiceProvider, CancellationToken, Task<double>> measure,
        Action<ProbeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(measure);
        var options = new ProbeOptions();
        configure?.Invoke(options);
        return Register(builder, source, "probe", measure, options);
    }

    /// <summary>
    /// Calls <paramref name="call"/> periodically and publishes how long it took, in milliseconds, as the value of a
    /// signal from <paramref name="source"/>. Thresholds in <see cref="ProbeOptions"/> are milliseconds. A call that
    /// throws or times out publishes <see cref="ProbeOptions.FailureSeverity"/> without a value.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="source">Source of the published signals.</param>
    /// <param name="call">A cheap call to the dependency, for example a ping. It runs in its own service scope.</param>
    /// <param name="configure">Changes interval, timeout and thresholds (in milliseconds).</param>
    /// <example>
    /// <code>
    /// h.AddLatencyProbe("search-index", (sp, ct) => sp.GetRequiredService&lt;SearchClient&gt;().PingAsync(ct),
    ///     p => p.DegradedAbove = 250);
    /// </code>
    /// </example>
    public static HomeostatBuilder AddLatencyProbe(
        this HomeostatBuilder builder,
        string source,
        Func<IServiceProvider, CancellationToken, Task> call,
        Action<ProbeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(call);

        var options = new ProbeOptions();
        configure?.Invoke(options);
        return Register(builder, source, "latency-probe", async (sp, ct) =>
        {
            var time = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var started = time.GetTimestamp();
            await call(sp, ct).ConfigureAwait(false);
            return time.GetElapsedTime(started).TotalMilliseconds;
        }, options);
    }

    private static HomeostatBuilder Register(
        HomeostatBuilder builder,
        string source,
        string defaultOrigin,
        Func<IServiceProvider, CancellationToken, Task<double>> measure,
        ProbeOptions options)
    {
        if (builder.Services.Any(d => d.ServiceType == typeof(ProbeRegistration)
            && d.ImplementationInstance is ProbeRegistration existing
            && string.Equals(existing.Source, source, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"A probe with source '{source}' is already registered.");
        }

        options.Validate(source);

        var settings = new ProbeOptions();
        options.CopyTo(settings);
        builder.Services.AddSingleton(new ProbeRegistration(source, settings.Origin ?? defaultOrigin, measure, settings));
        builder.AddDetector<ProbeDetector>();
        return builder;
    }
}
