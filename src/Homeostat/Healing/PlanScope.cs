namespace Homeostat.Healing;

/// <summary>Where a healing plan runs.</summary>
internal enum PlanScope
{
    /// <summary>Every instance heals its own condition independently (default).</summary>
    Instance = 0,

    /// <summary>
    /// One instance at a time across the cluster, with a shared cooldown, using the configured
    /// <see cref="Coordination.IHomeostatCoordinator"/> and <see cref="Persistence.IHomeostatStore"/>.
    /// </summary>
    Cluster = 1,
}
