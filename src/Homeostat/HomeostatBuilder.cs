using Homeostat.Conditions;
using Homeostat.Coordination;
using Homeostat.Detectors;
using Homeostat.Guardrails;
using Homeostat.Persistence;
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

    // ---------------------------------------------------------------- state and coordination

    /// <summary>Uses a custom <see cref="IHomeostatStore"/> (registered as a singleton) instead of the in-memory one.</summary>
    /// <typeparam name="TStore">The store type.</typeparam>
    public HomeostatBuilder UseStore<TStore>()
        where TStore : class, IHomeostatStore
    {
        Services.Replace(ServiceDescriptor.Singleton<IHomeostatStore, TStore>());
        return this;
    }

    /// <summary>Uses a store created by a factory.</summary>
    /// <param name="factory">Creates the store.</param>
    public HomeostatBuilder UseStore(Func<IServiceProvider, IHomeostatStore> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    /// <summary>
    /// Keeps cooldowns, backoff, holds, budget and history in a JSON file, so a single instance remembers them across
    /// restarts. For several instances, use a shared store such as <c>Homeostat.Redis</c>.
    /// </summary>
    /// <param name="path">File path, for example <c>"data/homeostat.json"</c>.</param>
    public HomeostatBuilder UseFileStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return UseStore(_ => new FileHomeostatStore(path));
    }

    /// <summary>Uses a custom <see cref="IHomeostatCoordinator"/> (registered as a singleton).</summary>
    /// <typeparam name="TCoordinator">The coordinator type.</typeparam>
    public HomeostatBuilder UseCoordinator<TCoordinator>()
        where TCoordinator : class, IHomeostatCoordinator
    {
        Services.Replace(ServiceDescriptor.Singleton<IHomeostatCoordinator, TCoordinator>());
        return this;
    }

    /// <summary>Uses a coordinator created by a factory.</summary>
    /// <param name="factory">Creates the coordinator.</param>
    public HomeostatBuilder UseCoordinator(Func<IServiceProvider, IHomeostatCoordinator> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    // ---------------------------------------------------------------- guardrails

    /// <summary>
    /// Adds a recurring maintenance window, during which Homeostat behaves as paused.
    /// </summary>
    /// <param name="name">Name shown in logs and status.</param>
    /// <param name="configure">Sets the schedule.</param>
    /// <example>
    /// <code>
    /// .AddMaintenanceWindow("nightly-deploy", w => w
    ///     .Daily(new TimeOnly(2, 0), TimeSpan.FromHours(1))
    ///     .On(DayOfWeek.Tuesday, DayOfWeek.Thursday)
    ///     .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome")))
    /// </code>
    /// </example>
    public HomeostatBuilder AddMaintenanceWindow(string name, Action<MaintenanceWindow> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var window = new MaintenanceWindow(name);
        configure(window);
        if (window.Duration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"Maintenance window '{name}' needs a schedule, for example Daily(start, duration).");
        }

        Services.AddSingleton(window);
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
