using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Homeostat.Signals;

/// <summary>
/// In-process buffer between signal publishers and the engine.
/// </summary>
internal sealed class SignalChannel : ISignalPublisher
{
    private readonly Channel<Signal> _channel;
    private readonly TimeProvider _timeProvider;

    public SignalChannel(IOptions<HomeostatOptions> options, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _channel = Channel.CreateBounded<Signal>(new BoundedChannelOptions(options.Value.SignalBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<Signal> Reader => _channel.Reader;

    public bool TryPublish(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return _channel.Writer.TryWrite(signal);
    }

    public bool TryPublish(string source, SignalSeverity severity, string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return TryPublish(new Signal(source, severity, _timeProvider.GetUtcNow()) { Message = message });
    }

    public void Complete() => _channel.Writer.TryComplete();
}
