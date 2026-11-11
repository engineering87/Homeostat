using Homeostat.Coordination;
using Homeostat.Persistence;
using Homeostat.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Homeostat;

/// <summary>Registers the Redis store and coordinator.</summary>
public static class HomeostatRedisExtensions
{
    /// <summary>
    /// Keeps cooldowns, backoff, holds, budget and run history in Redis, shared by every instance with the same
    /// key prefix, and coordinates plans marked with <c>OncePerCluster()</c>. Uses the
    /// <see cref="IConnectionMultiplexer"/> registered in the container.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Changes the settings.</param>
    /// <example>
    /// <code>
    /// // abortConnect=false: the application starts, and the client reconnects in the background, while Redis is down.
    /// builder.Services.AddSingleton&lt;IConnectionMultiplexer&gt;(_ => ConnectionMultiplexer.Connect("redis:6379,abortConnect=false"));
    /// builder.Services.AddHomeostat(h => h.UseRedis(o => o.KeyPrefix = "weather-api:homeostat:"));
    /// </code>
    /// </example>
    public static HomeostatBuilder UseRedis(this HomeostatBuilder builder, Action<RedisStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return Register(builder, sp => sp.GetRequiredService<IConnectionMultiplexer>(), configure);
    }

    /// <summary>
    /// Same as <see cref="UseRedis(HomeostatBuilder, Action{RedisStoreOptions}?)"/>, connecting to Redis with a
    /// connection string. The store and the coordinator use a client of their own, created with
    /// <c>abortConnect=false</c>, even when the application registers an <see cref="IConnectionMultiplexer"/> of its
    /// own (for a cache, for example); that client is also registered as the <see cref="IConnectionMultiplexer"/> when
    /// the application has none.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="connectionString">StackExchange.Redis configuration, for example <c>"redis:6379"</c>.</param>
    /// <param name="configure">Changes the settings.</param>
    public static HomeostatBuilder UseRedis(this HomeostatBuilder builder, string connectionString, Action<RedisStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // The store and the coordinator use a client of their own, whatever else is registered, before or after: an
        // application's IConnectionMultiplexer (a cache, perhaps with an eviction policy that could drop the pause
        // record and read as "not paused") must not receive the control state. The last call wins, like the store.
        builder.Services.Replace(ServiceDescriptor.Singleton(_ => new OwnRedisConnection(connectionString)));

        // AddRedisSignals pings the registered IConnectionMultiplexer: this client, unless the application has its own.
        builder.Services.TryAddSingleton<IConnectionMultiplexer>(sp => sp.GetRequiredService<OwnRedisConnection>().Get());
        return Register(builder, sp => sp.GetRequiredService<OwnRedisConnection>().Get(), configure);
    }

    private static HomeostatBuilder Register(HomeostatBuilder builder, Func<IServiceProvider, IConnectionMultiplexer> connection, Action<RedisStoreOptions>? configure)
    {
        var options = builder.Services.AddOptions<RedisStoreOptions>()
            .Validate(o => !string.IsNullOrWhiteSpace(o.KeyPrefix), "KeyPrefix cannot be empty.")
            .Validate(o => o.PlanStateRetention > TimeSpan.Zero, "PlanStateRetention must be positive.")
            .Validate(o => o.DocumentRetention > TimeSpan.Zero, "DocumentRetention must be positive.")
            .ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        // The connection is resolved on first use, on Homeostat's threads: the application starts even while Redis is
        // unreachable (connecting can take seconds), and Homeostat waits, paused, until it can read the control state.
        builder.Services.Replace(ServiceDescriptor.Singleton<IHomeostatStore>(sp => new RedisHomeostatStore(
            () => connection(sp),
            sp.GetRequiredService<IOptions<RedisStoreOptions>>(),
            sp.GetRequiredService<IOptions<HomeostatOptions>>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<RedisHomeostatStore>>())));
        builder.Services.Replace(ServiceDescriptor.Singleton<IHomeostatCoordinator>(sp => new RedisCoordinator(
            () => connection(sp),
            sp.GetRequiredService<IOptions<RedisStoreOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RedisCoordinator>>())));
        return builder;
    }

    /// <summary>
    /// Publishes the health of the Redis client as signals from <see cref="RedisSignalOptions.Source"/> (<c>redis</c>): a periodic <c>PING</c>
    /// whose round trip in milliseconds is the signal value (thresholds in <see cref="RedisSignalOptions"/>), and,
    /// when <see cref="RedisSignalOptions.PublishConnectionEvents"/> is on, an immediate signal when the client loses
    /// or restores a connection. A client that is not connected, or a ping that fails or times out, publishes
    /// <see cref="Probes.ProbeOptions.FailureSeverity"/>. Uses the <see cref="IConnectionMultiplexer"/> registered in
    /// the container, which can be the same one as <c>UseRedis</c> or the one your application uses for caching.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Changes interval, timeout, thresholds and connection events.</param>
    /// <example>
    /// <code>
    /// h.AddRedisSignals(o => { o.Source = "session-cache"; o.DegradedLatency = TimeSpan.FromMilliseconds(50); })
    ///  .AddCondition("session-cache-down", c => c.ForSource("session-cache").SustainedFor(TimeSpan.FromSeconds(30))
    ///      .Heal(p => p.TurnOnSwitch("sessions-in-memory", turnOffWhenCleared: true)));
    /// </code>
    /// </example>
    public static HomeostatBuilder AddRedisSignals(this HomeostatBuilder builder, Action<RedisSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new RedisSignalOptions();
        configure?.Invoke(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Source, nameof(RedisSignalOptions.Source));
        var source = options.Source;

        options.Origin ??= "redis-ping";
        var events = options.PublishConnectionEvents ? new RedisConnectionEventsPublisher(source, options.FailureSeverity) : null;

        builder.AddLatencyProbe(source, async (sp, _) =>
        {
            var connection = sp.GetRequiredService<IConnectionMultiplexer>();
            events?.EnsureSubscribed(connection, sp);
            if (!connection.IsConnected)
            {
                throw new InvalidOperationException("The Redis client is not connected.");
            }

            await connection.GetDatabase().PingAsync().ConfigureAwait(false);
        }, options.CopyTo);

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, RedisSignalsValidator>());
        return builder;
    }
}
