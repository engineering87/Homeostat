using Homeostat.Signals;

namespace Homeostat.Tests;

/// <summary>Signal publisher that keeps everything it receives, for detector tests.</summary>
internal sealed class CollectingPublisher : ISignalPublisher
{
    private readonly object _gate = new();
    private readonly List<Signal> _signals = [];

    public IReadOnlyList<Signal> Signals
    {
        get
        {
            lock (_gate)
            {
                return _signals.ToArray();
            }
        }
    }

    public bool TryPublish(Signal signal)
    {
        lock (_gate)
        {
            _signals.Add(signal);
        }

        return true;
    }

    public bool TryPublish(string source, SignalSeverity severity, string? message = null, double? value = null)
        => TryPublish(new Signal(source, severity, DateTimeOffset.UtcNow) { Message = message, Value = value });
}
