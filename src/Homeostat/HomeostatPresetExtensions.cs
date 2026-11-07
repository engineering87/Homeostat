using Homeostat.Presets;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat;

/// <summary>Starter presets: the detectors and conditions most applications need, in one line.</summary>
public static class HomeostatPresetExtensions
{
    /// <summary>
    /// Registers the runtime detectors (thread pool, memory, exceptions), the ASP.NET Core error rate and latency
    /// signals, and a condition for each (see <see cref="HomeostatDefaults"/>). The conditions only observe: they are
    /// tracked, logged and shown by the endpoints, and none acts until you give it a plan.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Turns conditions off, changes how long problems must last, adds plans; optional.</param>
    /// <returns>The builder.</returns>
    /// <remarks>
    /// Do not also call the detector or signal methods that the preset calls (<c>AddServerErrorRateSignal</c> and the
    /// others): the server signals would be published twice. To make a condition trigger at a different level, change
    /// its rule and keep the rest of the preset, suppression included:
    /// <c>d.ServerErrors.Configure(c => c.WhenValueAbove(0.10))</c> (the value is the share of 5xx responses) or
    /// <c>d.ServerLatency.Configure(c => c.WhenValueAbove(1.5))</c> (the p95 in seconds). Only one preset can be added
    /// to an application.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddHomeostat(h => h.AddWebApiDefaults(d =>
    /// {
    ///     d.MemoryPressure.Enabled = false;
    ///     d.ServerErrors.Heal(p => p.With&lt;RecycleDependencyClientsAction&gt;());
    /// }));
    /// </code>
    /// </example>
    public static HomeostatBuilder AddWebApiDefaults(this HomeostatBuilder builder, Action<HomeostatDefaults>? configure = null)
        => AddDefaults(builder, webApi: true, configure);

    /// <summary>
    /// Registers the runtime detectors (thread pool, memory, exceptions), the state of every
    /// <see cref="RestartableBackgroundService"/> (sources <c>hosted-service:{type name}</c>), and a condition for each
    /// runtime detector (see <see cref="HomeostatDefaults"/>). The conditions only observe until you give them a plan.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Turns conditions off, changes how long problems must last, adds plans; optional.</param>
    /// <returns>The builder.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddHomeostat(h => h
    ///     .AddWorkerDefaults()
    ///     .AddCondition("ingest-worker-stopped", c => c
    ///         .ForSource("hosted-service:IngestWorker")
    ///         .Heal(p => p.RestartHostedService&lt;IngestWorker&gt;())));
    /// </code>
    /// </example>
    public static HomeostatBuilder AddWorkerDefaults(this HomeostatBuilder builder, Action<HomeostatDefaults>? configure = null)
    {
        AddDefaults(builder, webApi: false, configure);
        return builder.AddBackgroundServiceSignals();
    }

    private static HomeostatBuilder AddDefaults(HomeostatBuilder builder, bool webApi, Action<HomeostatDefaults>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Services.Any(d => d.ServiceType == typeof(PresetMarker)))
        {
            throw new InvalidOperationException(
                "A starter preset (AddWebApiDefaults or AddWorkerDefaults) is already registered: add only one, and configure it through its callback.");
        }

        builder.Services.AddSingleton(new PresetMarker());
        var defaults = new HomeostatDefaults(webApi);
        configure?.Invoke(defaults);

        // Causes before symptoms: a starved thread pool or memory pressure makes requests slow and failing, so the
        // server conditions wait while those are active instead of acting on the symptom.
        var causes = new List<string>();
        if (defaults.ThreadPoolStarvation.Enabled)
        {
            builder.AddThreadPoolStarvationDetector();
            Add(builder, defaults.ThreadPoolStarvation, []);
            causes.Add(defaults.ThreadPoolStarvation.Name);
        }

        if (defaults.MemoryPressure.Enabled)
        {
            builder.AddMemoryPressureDetector();
            Add(builder, defaults.MemoryPressure, []);
        }

        if (defaults.ExceptionRate.Enabled)
        {
            builder.AddExceptionRateDetector();
            Add(builder, defaults.ExceptionRate, []);
        }

        if (defaults.ServerErrors.Enabled)
        {
            builder.AddServerErrorRateSignal();
            Add(builder, defaults.ServerErrors, causes);
        }

        if (defaults.ServerLatency.Enabled)
        {
            builder.AddServerLatencySignal();
            Add(builder, defaults.ServerLatency, defaults.MemoryPressure.Enabled ? [.. causes, defaults.MemoryPressure.Name] : causes);
        }

        return builder;
    }

    private static void Add(HomeostatBuilder builder, DefaultCondition condition, IReadOnlyList<string> suppressedBy)
        => builder.AddCondition(condition.Name, c => condition.Apply(c, suppressedBy));

    /// <summary>Marks that a preset is registered, so a second one fails clearly instead of duplicating conditions.</summary>
    private sealed class PresetMarker;
}
