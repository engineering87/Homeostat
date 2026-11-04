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
    /// Convenience overload that stamps the signal with the current time. Implementations need not provide it: by
    /// default it uses the system clock and <see cref="TryPublish(Signal)"/>; Homeostat's own publisher uses the
    /// application's <see cref="TimeProvider"/>.
    /// </summary>
    /// <param name="source">Signal source.</param>
    /// <param name="severity">Signal severity.</param>
    /// <param name="message">Optional message.</param>
    /// <param name="value">Optional measured value (see <see cref="Signal.Value"/>).</param>
    bool TryPublish(string source, SignalSeverity severity, string? message = null, double? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return TryPublish(new Signal(source, severity, TimeProvider.System.GetUtcNow()) { Message = message, Value = value });
    }
}
