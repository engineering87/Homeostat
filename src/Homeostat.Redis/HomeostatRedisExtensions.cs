using Homeostat.Coordination;
using Homeostat.Persistence;
using Homeostat.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
    /// builder.Services.AddSingleton&lt;IConnectionMultiplexer&gt;(_ => ConnectionMultiplexer.Connect("redis:6379"));
    /// builder.Services.AddHomeostat(h => h.UseRedis(o => o.KeyPrefix = "weather-api:homeostat:"));
    /// </code>
    /// </example>
    public static HomeostatBuilder UseRedis(this HomeostatBuilder builder, Action<RedisStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Services.AddOptions<RedisStoreOptions>()
            .Validate(o => !string.IsNullOrWhiteSpace(o.KeyPrefix), "KeyPrefix cannot be empty.")
            .Validate(o => o.PlanStateRetention > TimeSpan.Zero, "PlanStateRetention must be positive.")
            .Validate(o => o.DocumentRetention > TimeSpan.Zero, "DocumentRetention must be positive.")
            .ValidateOnStart();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        builder.Services.Replace(ServiceDescriptor.Singleton<IHomeostatStore, RedisHomeostatStore>());
        builder.Services.Replace(ServiceDescriptor.Singleton<IHomeostatCoordinator, RedisCoordinator>());
        return builder;
    }

    /// <summary>Same as <see cref="UseRedis(HomeostatBuilder, Action{RedisStoreOptions}?)"/>, connecting to Redis with a connection string.</summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="connectionString">StackExchange.Redis configuration, for example <c>"redis:6379"</c>.</param>
    /// <param name="configure">Changes the settings.</param>
    public static HomeostatBuilder UseRedis(this HomeostatBuilder builder, string connectionString, Action<RedisStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // AbortOnConnectFail = false: the application starts even when Redis does not; the store fails closed and
        // the client keeps reconnecting in the background.
        builder.Services.TryAddSingleton<IConnectionMultiplexer>(_ =>
        {
            var configuration = ConfigurationOptions.Parse(connectionString);
            configuration.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(configuration);
        });
        return builder.UseRedis(configure);
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
    /// h.AddRedisSignals(o => { o.Source = "session-cache"; o.DegradedAbove = 50; })
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
