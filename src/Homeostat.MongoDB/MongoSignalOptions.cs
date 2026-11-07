using Homeostat.Probes;
using MongoDB.Driver;

namespace Homeostat.MongoDB;

/// <summary>Settings of the MongoDB signals.</summary>
public sealed class MongoSignalOptions
{
    /// <summary>Returns the client; by default the <see cref="IMongoClient"/> registered in the services.</summary>
    public Func<IServiceProvider, IMongoClient>? Client { get; set; }

    /// <summary>
    /// Source prefix of the signals: <c>{prefix}:ping</c>, <c>{prefix}:primary</c>, <c>{prefix}:pool</c> and
    /// <c>{prefix}:replication-lag</c>. Defaults to <c>mongodb:{name}</c>.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>Thresholds of the ping round trip, in milliseconds (<c>mongodb:{name}:ping</c>).</summary>
    public LatencyProbeOptions Ping { get; } = new();

    /// <summary>
    /// Publishes the replication lag of the replica set, in seconds (<c>mongodb:{name}:replication-lag</c>): how far
    /// the furthest secondary is behind the primary. It runs <c>replSetGetStatus</c>, which needs the
    /// <c>clusterMonitor</c> role. Defaults to <c>false</c>.
    /// </summary>
    public bool ReplicationLag { get; set; }

    /// <summary>Thresholds of the replication lag, in seconds.</summary>
    public ProbeOptions Lag { get; } = new();

    /// <summary>
    /// Members (their <c>host:port</c> name) left out of the replication lag, such as delayed members, which lag on
    /// purpose.
    /// </summary>
    public ISet<string> LagExcludedMembers { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Connection checkouts that timed out within an <see cref="Interval"/> (the pool was exhausted) above which
    /// <c>mongodb:{name}:pool</c> is Unhealthy; defaults to 0: any timeout. Needs the client events
    /// (<c>AddHomeostatSignals</c> on the client settings).
    /// </summary>
    public int PoolTimeoutsUnhealthyAbove { get; set; }

    /// <summary>
    /// How often the probes run and the event-based signals are published; defaults to 30 seconds. An <c>Interval</c>
    /// set on <see cref="Ping"/> or <see cref="Lag"/> wins over this one for that probe.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);
}
