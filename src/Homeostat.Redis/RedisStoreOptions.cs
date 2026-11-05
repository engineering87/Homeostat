namespace Homeostat.Redis;

/// <summary>Settings of the Redis store and coordinator.</summary>
public sealed class RedisStoreOptions
{
    /// <summary>
    /// Prefix of every key. Instances that share the prefix share cooldowns, budget, history and leases; use one
    /// prefix per application (and environment). Defaults to <c>"homeostat:"</c>.
    /// </summary>
    public string KeyPrefix { get; set; } = "homeostat:";

    /// <summary>
    /// Whether the action budget is shared by every instance (the default) or counted per instance.
    /// </summary>
    public bool ShareBudget { get; set; } = true;

    /// <summary>
    /// How long plan state is kept after its last change; refreshed at every run. A held plan is kept until its hold
    /// ends. Defaults to 7 days.
    /// </summary>
    public TimeSpan PlanStateRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long documents (such as the learned baselines of seasonal rules) are kept after their last update. Defaults
    /// to 90 days, longer than the weeks a weekly baseline needs to learn.
    /// </summary>
    public TimeSpan DocumentRetention { get; set; } = TimeSpan.FromDays(90);
}
