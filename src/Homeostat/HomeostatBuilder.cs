using System.Diagnostics.CodeAnalysis;
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

    /// <summary>Changes global settings and guardrails, such as dry-run, the action budget and default timeouts.</summary>
    /// <param name="configure">Changes the <see cref="HomeostatOptions"/>; can be called several times.</param>
    /// <returns>This builder.</returns>
    /// <example><code>h.Configure(o => { o.DryRun = false; o.MaxActionsPerWindow = 5; })</code></example>
    public HomeostatBuilder Configure(Action<HomeostatOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }

    /// <summary>
    /// Registers a condition (when a problem is real: which signals, which rule, for how long) and, optionally, the
    /// healing plan that runs while it is active.
    /// </summary>
    /// <param name="name">Unique name of the condition, used in logs, metrics, endpoints and the store.</param>
    /// <param name="configure">Selects the signals, sets the rules and, with <see cref="ConditionBuilder.Heal"/>, the plan.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">The condition has neither a source nor a clear rule, or its rules conflict.</exception>
    /// <example>
    /// <code>
    /// h.AddCondition("reports-db-slow", c => c
    ///     .ForSource("reports-db")
    ///     .WhenValueAbove(0.5)                         // seconds, from a probe
    ///     .SustainedFor(TimeSpan.FromMinutes(2))
    ///     .ClearAfter(TimeSpan.FromMinutes(1))
    ///     .Heal(p => p.ClearConnectionPools&lt;NpgsqlConnection&gt;()));
    /// </code>
    /// </example>
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
    public HomeostatBuilder UseStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>()
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
    /// <param name="configure">Changes the retention settings; optional.</param>
    /// <example><code>h.UseFileStore("data/homeostat.json", o => o.PlanStateRetention = TimeSpan.FromDays(7))</code></example>
    public HomeostatBuilder UseFileStore(string path, Action<FileStoreOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var options = new FileStoreOptions();
        configure?.Invoke(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PlanStateRetention, TimeSpan.Zero, nameof(configure));
        return UseStore(_ => new FileHomeostatStore(path, options));
    }

    /// <summary>Uses a custom <see cref="IHomeostatCoordinator"/> (registered as a singleton).</summary>
    /// <typeparam name="TCoordinator">The coordinator type.</typeparam>
    public HomeostatBuilder UseCoordinator<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCoordinator>()
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

    /// <summary>
    /// Publishes the state of every <see cref="RestartableBackgroundService"/> registered with <c>AddHostedService</c>:
    /// Healthy while it runs, Unhealthy when its loop threw or returned. The source is <c>hosted-service:{type name}</c>,
    /// so a condition can restart a worker that crashed.
    /// </summary>
    /// <param name="configure">Source prefix; optional.</param>
    /// <example>
    /// <code>
    /// h.AddBackgroundServiceSignals()
    ///  .AddCondition("ingest-crashed", c => c.ForSource("hosted-service:IngestWorker")
    ///      .Heal(p => p.RestartHostedService&lt;IngestWorker&gt;()));
    /// </code>
    /// </example>
    public HomeostatBuilder AddBackgroundServiceSignals(Action<BackgroundServiceSignalOptions>? configure = null)
        => AddBuiltInDetector<BackgroundServiceSignalDetector, BackgroundServiceSignalOptions>(configure);

    /// <summary>
    /// Registers a custom <see cref="ISignalDetector"/> (a singleton): the engine calls it at every
    /// <see cref="HomeostatOptions.DetectionInterval"/> and it publishes the signals it measures.
    /// </summary>
    /// <typeparam name="TDetector">The detector type, created from the services.</typeparam>
    /// <returns>This builder.</returns>
    public HomeostatBuilder AddDetector<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDetector>()
        where TDetector : class, ISignalDetector
    {
        Services.AddHomeostatDetector<TDetector>();
        return this;
    }

    // ---------------------------------------------------------------- observers

    /// <summary>
    /// Registers an observer of conditions, steps, runs and approvals. <typeparamref name="TObserver"/> is resolved
    /// from the container (registered as a singleton unless you registered it yourself), so you can also inject it
    /// elsewhere. Registering the same type twice has no effect.
    /// </summary>
    /// <typeparam name="TObserver">The observer type.</typeparam>
    /// <returns>This builder.</returns>
    public HomeostatBuilder AddObserver<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TObserver>()
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

    private HomeostatBuilder AddBuiltInDetector<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TDetector, TOptions>(Action<TOptions>? configure)
        where TDetector : class, ISignalDetector
        where TOptions : class
    {
        var options = Services.AddOptions<TOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        Services.AddHomeostatDetector<TDetector>();
        return this;
    }
}
