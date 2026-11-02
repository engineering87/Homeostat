namespace Homeostat.Coordination;

/// <summary>
/// Gives exclusive leases on named resources, so that a plan marked with <c>OncePerCluster()</c> runs on one
/// instance at a time. The default coordinator only coordinates within the process; a distributed one (for
/// example <c>Homeostat.Redis</c>) coordinates every instance that shares it.
/// </summary>
public interface IHomeostatCoordinator
{
    /// <summary>
    /// Tries to acquire an exclusive lease. Returns <c>null</c> if another holder has it. The lease is kept alive
    /// until it is disposed; implementations renew it in the background and let it expire if the holder dies.
    /// </summary>
    /// <param name="resource">Name of the resource.</param>
    /// <param name="duration">How long the lease lasts without renewal.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<IHomeostatLease?> TryAcquireAsync(string resource, TimeSpan duration, CancellationToken cancellationToken = default);
}

/// <summary>An exclusive lease; dispose it to release it.</summary>
public interface IHomeostatLease : IAsyncDisposable
{
    /// <summary>
    /// Cancelled when the lease may have been lost (it could not be renewed in time), so another holder may already
    /// have it. Homeostat stops the run before its next step when this happens.
    /// </summary>
    CancellationToken Lost { get; }
}
