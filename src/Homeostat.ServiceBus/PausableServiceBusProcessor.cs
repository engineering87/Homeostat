using Azure.Messaging.ServiceBus;
using Homeostat.Consumers;

namespace Homeostat.ServiceBus;

/// <summary>A Service Bus processor or session processor that Homeostat can pause and resume.</summary>
public sealed class PausableServiceBusProcessor : IPausableConsumer
{
    private readonly Func<CancellationToken, Task> _stop;
    private readonly Func<CancellationToken, Task> _start;

    /// <summary>Wraps a processor.</summary>
    /// <param name="name">Consumer name.</param>
    /// <param name="processor">The processor; its handlers must already be set.</param>
    public PausableServiceBusProcessor(string name, ServiceBusProcessor processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(processor);
        Name = name;
        _stop = ct => processor.IsProcessing ? processor.StopProcessingAsync(ct) : Task.CompletedTask;
        _start = ct => processor.IsProcessing ? Task.CompletedTask : processor.StartProcessingAsync(ct);
    }

    /// <summary>Wraps a session processor.</summary>
    /// <param name="name">Consumer name.</param>
    /// <param name="processor">The session processor; its handlers must already be set.</param>
    public PausableServiceBusProcessor(string name, ServiceBusSessionProcessor processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(processor);
        Name = name;
        _stop = ct => processor.IsProcessing ? processor.StopProcessingAsync(ct) : Task.CompletedTask;
        _start = ct => processor.IsProcessing ? Task.CompletedTask : processor.StartProcessingAsync(ct);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Stops processing: waits for the messages being handled.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task PauseAsync(CancellationToken cancellationToken) => _stop(cancellationToken);

    /// <summary>Starts processing again.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task ResumeAsync(CancellationToken cancellationToken) => _start(cancellationToken);
}
