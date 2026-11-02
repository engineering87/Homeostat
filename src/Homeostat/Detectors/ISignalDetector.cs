using Homeostat.Signals;

namespace Homeostat.Detectors;

/// <summary>
/// Samples part of the application at a fixed interval (<see cref="HomeostatOptions.DetectionInterval"/>)
/// and publishes signals. Register custom detectors with <c>AddDetector&lt;T&gt;()</c>.
/// </summary>
public interface ISignalDetector
{
    /// <summary>Takes one sample and publishes zero or more signals.</summary>
    ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken);
}
