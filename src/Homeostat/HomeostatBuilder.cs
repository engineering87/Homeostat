using Homeostat.Conditions;
using Homeostat.Detectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Homeostat;

/// <summary>Configures Homeostat. Returned by <see cref="HomeostatServiceCollectionExtensions.AddHomeostat"/>.</summary>
public sealed class HomeostatBuilder
{
    internal HomeostatBuilder(IServiceCollection services) => Services = services;

    /// <summary>The service collection, to register the services your actions need.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Changes global settings and guardrails.</summary>
    public HomeostatBuilder Configure(Action<HomeostatOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }

    /// <summary>Registers a condition and, optionally, the plan that heals it.</summary>
    public HomeostatBuilder AddCondition(string name, Action<ConditionBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new ConditionBuilder(name, Services);
        configure(builder);
        Services.AddSingleton(builder.Build());
        return this;
    }

    // ---------------------------------------------------------------- detectors

    /// <summary>
    /// Publishes signals about thread pool starvation (source <c>"threadpool"</c> by default),
    /// measured as the time a work item waits before running.
    /// </summary>
    public HomeostatBuilder AddThreadPoolStarvationDetector(Action<ThreadPoolDetectorOptions>? configure = null)
        => AddBuiltInDetector<ThreadPoolStarvationDetector, ThreadPoolDetectorOptions>(configure);

    /// <summary>Publishes signals about memory load (source <c>"memory"</c> by default).</summary>
    public HomeostatBuilder AddMemoryPressureDetector(Action<MemoryPressureDetectorOptions>? configure = null)
        => AddBuiltInDetector<MemoryPressureDetector, MemoryPressureDetectorOptions>(configure);

    /// <summary>Publishes signals about the rate of thrown exceptions (source <c>"exceptions"</c> by default).</summary>
    public HomeostatBuilder AddExceptionRateDetector(Action<ExceptionRateDetectorOptions>? configure = null)
        => AddBuiltInDetector<ExceptionRateDetector, ExceptionRateDetectorOptions>(configure);

    /// <summary>Registers a custom <see cref="ISignalDetector"/>.</summary>
    public HomeostatBuilder AddDetector<TDetector>()
        where TDetector : class, ISignalDetector
    {
        Services.AddSignalDetector<TDetector>();
        return this;
    }

    // ---------------------------------------------------------------- observers

    /// <summary>
    /// Registers an observer of conditions, steps, runs and approvals. <typeparamref name="TObserver"/> is resolved
    /// from the container (registered as a singleton unless you registered it yourself), so you can also inject it
    /// elsewhere. Registering the same type twice has no effect.
    /// </summary>
    public HomeostatBuilder AddObserver<TObserver>()
        where TObserver : class, IHealingObserver
    {
        if (Services.Any(d => d.ServiceType == typeof(ObserverMarker<TObserver>)))
        {
            return this;
        }

        Services.AddSingleton<ObserverMarker<TObserver>>();
        Services.TryAddSingleton<TObserver>();
        Services.AddSingleton<IHealingObserver>(sp => sp.GetRequiredService<TObserver>());
        return this;
    }

    private sealed class ObserverMarker<TObserver>;

    private HomeostatBuilder AddBuiltInDetector<TDetector, TOptions>(Action<TOptions>? configure)
        where TDetector : class, ISignalDetector
        where TOptions : class
    {
        var options = Services.AddOptions<TOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        Services.AddSignalDetector<TDetector>();
        return this;
    }
}
