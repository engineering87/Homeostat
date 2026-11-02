using System.Collections.Concurrent;
using MongoDB.Driver.Core.Configuration;

namespace Homeostat.MongoDB;

/// <summary>
/// What the driver's events told about each named deployment: whether a writable server (primary, mongos, standalone)
/// is connected in every cluster the driver opened for it, and how many connection checkouts timed out.
/// </summary>
internal sealed class MongoClientEvents
{
    private readonly ConcurrentDictionary<string, State> _clients = new(StringComparer.Ordinal);

    // The driver caches clusters by their settings, comparing ClusterConfigurator by reference: the same settings must
    // get the same configurator, or every client would open a cluster of its own that is never released.
    private readonly ConcurrentDictionary<(string Name, Delegate? Previous), Action<ClusterBuilder>> _configurators = new();

    public State For(string name) => _clients.GetOrAdd(name, static _ => new State());

    public bool TryGet(string name, out State state) => _clients.TryGetValue(name, out state!);

    public Action<ClusterBuilder> Configurator(string name, Action<ClusterBuilder>? previous, Func<Action<ClusterBuilder>> create)
        => _configurators.GetOrAdd((name, previous), _ => create());

    internal sealed class State
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, (bool Writable, int Connected)> _clusters = [];
        private int _timeouts;
        private bool? _writable;

        /// <summary>Whether a client is subscribed to the driver events.</summary>
        public bool Subscribed { get; set; }

        /// <summary><c>null</c> until a cluster has heard from a server.</summary>
        public bool? Writable
        {
            get
            {
                lock (_gate)
                {
                    return _writable;
                }
            }
        }

        public int ConnectedServers
        {
            get
            {
                lock (_gate)
                {
                    return _clusters.Values.Sum(c => c.Connected);
                }
            }
        }

        public void CheckoutFailed(bool timedOut)
        {
            if (timedOut)
            {
                Interlocked.Increment(ref _timeouts);
            }
        }

        /// <summary>Returns the timeouts since the last call.</summary>
        public int TakeTimeouts() => Interlocked.Exchange(ref _timeouts, 0);

        /// <summary>
        /// Records a new description of one cluster; the deployment is writable only if every cluster is (the worst
        /// wins). Returns whether writability changed.
        /// </summary>
        public bool ClusterChanged(int clusterId, bool writable, int connectedServers)
        {
            lock (_gate)
            {
                _clusters[clusterId] = (writable, connectedServers);
                var now = _clusters.Values.All(c => c.Writable);
                var changed = _writable != now;
                _writable = now;
                return changed;
            }
        }
    }
}
