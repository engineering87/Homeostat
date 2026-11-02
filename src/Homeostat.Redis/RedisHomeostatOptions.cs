namespace Homeostat.Redis;

/// <summary>Settings of the Redis store and coordinator.</summary>
public sealed class RedisHomeostatOptions
{
    /// <summary>
    /// Prefix of every key. Instances that share the prefix share cooldowns, budget, history and leases; use one
    /// prefix per application (and environment).
    /// </summary>
    public string KeyPrefix { get; set; } = "homeostat:";

    /// <summary>
    /// Whether the action budget is shared by every instance (the default) or counted per instance.
    /// </summary>
    public bool ShareBudget { get; set; } = true;

    /// <summary>How long plan state is kept after its last change; refreshed at every run.</summary>
    public TimeSpan PlanStateRetention { get; set; } = TimeSpan.FromDays(7);
}
