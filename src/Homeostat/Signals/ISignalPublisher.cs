namespace Homeostat.Signals;

/// <summary>
/// Entry point for feeding observations into Homeostat. Any component can publish signals:
/// health checks, metric listeners, message consumers or your own code.
/// </summary>
public interface ISignalPublisher
{
    /// <summary>
    /// Publishes a signal without waiting. Returns <c>false</c> only if Homeostat is shutting down.
    /// When the internal buffer is full, the oldest pending signal is dropped.
    /// </summary>
    bool TryPublish(Signal signal);

    /// <summary>
    /// Convenience overload that stamps the signal with the current time.
    /// </summary>
    bool TryPublish(string source, SignalSeverity severity, string? message = null);
}
