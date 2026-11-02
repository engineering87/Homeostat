using Homeostat.Detectors;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Calls;

/// <summary>One <c>AddCallSignals</c> registration: the recorder's key and the origin tag of its signals.</summary>
internal sealed record CallSignalRegistration(string Source, string Origin);

/// <summary>Publishes one signal per call recorder at every detection interval.</summary>
internal sealed class CallSignalDetector(IEnumerable<CallSignalRegistration> registrations, IServiceProvider services, TimeProvider timeProvider)
    : ISignalDetector
{
    // The last registration of a source wins, like most configuration in .NET.
    private readonly (CallRecorder Recorder, string Origin)[] _recorders = registrations
        .GroupBy(r => r.Source, StringComparer.Ordinal)
        .Select(g => (services.GetRequiredKeyedService<CallRecorder>(g.Key), g.Last().Origin))
        .ToArray();

    public ValueTask DetectAsync(ISignalPublisher signals, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (recorder, origin) in _recorders)
        {
            if (recorder.Collect(now, origin) is { } signal)
            {
                signals.TryPublish(signal);
            }
        }

        return ValueTask.CompletedTask;
    }
}
