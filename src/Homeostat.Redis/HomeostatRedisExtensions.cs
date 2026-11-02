using Homeostat.Coordination;
using Homeostat.Persistence;
using Homeostat.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public static HomeostatBuilder UseRedis(this HomeostatBuilder builder, Action<RedisHomeostatOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Services.AddOptions<RedisHomeostatOptions>()
            .Validate(o => !string.IsNullOrWhiteSpace(o.KeyPrefix), "KeyPrefix cannot be empty.")
            .Validate(o => o.PlanStateRetention > TimeSpan.Zero, "PlanStateRetention must be positive.");
        if (configure is not null)
        {
            options.Configure(configure);
        }

        builder.Services.Replace(ServiceDescriptor.Singleton<IHomeostatStore, RedisHomeostatStore>());
        builder.Services.Replace(ServiceDescriptor.Singleton<IHomeostatCoordinator, RedisCoordinator>());
        return builder;
    }

    /// <summary>Same as <see cref="UseRedis(HomeostatBuilder, Action{RedisHomeostatOptions}?)"/>, connecting to Redis with a connection string.</summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="connectionString">StackExchange.Redis configuration, for example <c>"redis:6379"</c>.</param>
    /// <param name="configure">Changes the settings.</param>
    public static HomeostatBuilder UseRedis(this HomeostatBuilder builder, string connectionString, Action<RedisHomeostatOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        builder.Services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connectionString));
        return builder.UseRedis(configure);
    }
}
