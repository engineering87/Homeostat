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
        _stop = ct => Stop(() => processor.IsProcessing, processor.StopProcessingAsync, ct);
        _start = ct => Start(() => processor.IsProcessing, () => processor.IsClosed, processor.StartProcessingAsync, ct);
    }

    /// <summary>Wraps a session processor.</summary>
    /// <param name="name">Consumer name.</param>
    /// <param name="processor">The session processor; its handlers must already be set.</param>
    public PausableServiceBusProcessor(string name, ServiceBusSessionProcessor processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(processor);
        Name = name;
        _stop = ct => Stop(() => processor.IsProcessing, processor.StopProcessingAsync, ct);
        _start = ct => Start(() => processor.IsProcessing, () => processor.IsClosed, processor.StartProcessingAsync, ct);
    }

    // Set when Homeostat's pause stopped a running processor: a resume restarts only that, never a processor the
    // application stopped or closed itself.
    private volatile bool _stoppedByHomeostat;

    private Task Stop(Func<bool> isProcessing, Func<CancellationToken, Task> stop, CancellationToken cancellationToken)
    {
        if (!isProcessing())
        {
            return Task.CompletedTask;
        }

        _stoppedByHomeostat = true;
        return stop(cancellationToken);
    }

    private async Task Start(Func<bool> isProcessing, Func<bool> isClosed, Func<CancellationToken, Task> start, CancellationToken cancellationToken)
    {
        if (!_stoppedByHomeostat || isProcessing() || isClosed())
        {
            _stoppedByHomeostat = false;
            return;
        }

        await start(cancellationToken).ConfigureAwait(false);
        _stoppedByHomeostat = false;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Stops processing (<c>StopProcessingAsync</c>): no new message is received.</summary>
    /// <remarks>
    /// Stopping cancels the cancellation token passed to the message handlers and waits for them to return. Messages
    /// whose handler gives up on that token are abandoned and delivered again later (their delivery count grows), so
    /// handlers should complete or abandon promptly when the token is canceled.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task PauseAsync(CancellationToken cancellationToken) => _stop(cancellationToken);

    /// <summary>Starts processing again, if Homeostat's pause stopped it (not a processor the application stopped or closed).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task ResumeAsync(CancellationToken cancellationToken) => _start(cancellationToken);
}
