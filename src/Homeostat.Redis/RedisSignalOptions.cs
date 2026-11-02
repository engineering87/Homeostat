using Homeostat.Probes;

namespace Homeostat.Redis;

/// <summary>
/// Settings of the Redis client signals registered with <c>AddRedisSignals</c>. Thresholds are milliseconds of
/// <c>PING</c> round trip.
/// </summary>
public sealed class RedisSignalOptions : ProbeOptions
{
    /// <summary>
    /// Publishes a signal as soon as the client reports a lost or restored connection, without waiting for the next
    /// ping. Defaults to <c>true</c>.
    /// </summary>
    public bool PublishConnectionEvents { get; set; } = true;
}
