using Azure.Messaging.ServiceBus;
using Homeostat.Consumers;

namespace Homeostat.ServiceBus;

/// <summary>A Service Bus processor or session processor that Homeostat can pause and resume.</summary>
public sealed class PausableServiceBusProcessor : IPausableConsumer
{
    /// <summary>How often a pause that found the processor not yet started looks again.</summary>
    internal static readonly TimeSpan DefaultWatchInterval = TimeSpan.FromSeconds(1);

    /// <summary>Longest wait for a stop made by the watch: a stuck handler must not hold it forever.</summary>
    internal static readonly TimeSpan WatchStopTimeout = TimeSpan.FromMinutes(2);

    private readonly Func<bool> _isProcessing;
    private readonly Func<bool> _isClosed;
    private readonly Func<CancellationToken, Task> _stopProcessing;
    private readonly Func<CancellationToken, Task> _startProcessing;
    private readonly TimeSpan _watchInterval;
    private readonly object _gate = new();
    private CancellationTokenSource? _watchCancellation;
    private Task _watch = Task.CompletedTask;

    /// <summary>Wraps a processor.</summary>
    /// <param name="name">Consumer name.</param>
    /// <param name="processor">The processor; its handlers must already be set.</param>
    public PausableServiceBusProcessor(string name, ServiceBusProcessor processor)
        : this(name, processor, DefaultWatchInterval)
    {
    }

    /// <summary>Wraps a session processor.</summary>
    /// <param name="name">Consumer name.</param>
    /// <param name="processor">The session processor; its handlers must already be set.</param>
    public PausableServiceBusProcessor(string name, ServiceBusSessionProcessor processor)
        : this(name, processor, DefaultWatchInterval)
    {
    }

    internal PausableServiceBusProcessor(string name, ServiceBusProcessor processor, TimeSpan watchInterval)
        : this(name, watchInterval)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _isProcessing = () => processor.IsProcessing;
        _isClosed = () => processor.IsClosed;
        _stopProcessing = processor.StopProcessingAsync;
        _startProcessing = processor.StartProcessingAsync;
    }

    internal PausableServiceBusProcessor(string name, ServiceBusSessionProcessor processor, TimeSpan watchInterval)
        : this(name, watchInterval)
    {
        ArgumentNullException.ThrowIfNull(processor);
        _isProcessing = () => processor.IsProcessing;
        _isClosed = () => processor.IsClosed;
        _stopProcessing = processor.StopProcessingAsync;
        _startProcessing = processor.StartProcessingAsync;
    }

    private PausableServiceBusProcessor(string name, TimeSpan watchInterval)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _watchInterval = watchInterval;
        _isProcessing = _isClosed = static () => false;
        _stopProcessing = _startProcessing = static _ => Task.CompletedTask;
    }

    // Set when Homeostat's pause stopped a running processor: a resume restarts only that, never a processor the
    // application stopped or closed itself.
    private volatile bool _stoppedByHomeostat;

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Stops processing (<c>StopProcessingAsync</c>): no new message is received.</summary>
    /// <remarks>
    /// Stopping cancels the cancellation token passed to the message handlers and waits for them to return. Messages
    /// whose handler gives up on that token are abandoned and delivered again later (their delivery count grows), so
    /// handlers should complete or abandon promptly when the token is canceled. A pause that finds the processor not
    /// started yet (the switch was already on when the application started) stands: the processor is stopped as soon
    /// as the application starts it, until the pause ends.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        // The pause stands while the switch is on: the application may start the processor after this call (it was
        // not started yet when the switch was already on at startup), and it must not run while paused.
        StartWatch();

        if (_isProcessing())
        {
            _stoppedByHomeostat = true;
            await _stopProcessing(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Starts processing again, if Homeostat's pause stopped it (not a processor the application stopped or closed).</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        Task watch;
        lock (_gate)
        {
            _watchCancellation?.Cancel();
            _watchCancellation?.Dispose();
            _watchCancellation = null;
            watch = _watch;
        }

        // A stop made by the watch may still run: starting before it ends would let it stop the processor again
        // after the resume. The wait is bounded by the caller's token; if it ends first, the resume fails and is
        // retried, and the processor stays stopped meanwhile (fail closed).
        await watch.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (!_stoppedByHomeostat || _isProcessing() || _isClosed())
        {
            _stoppedByHomeostat = false;
            return;
        }

        await _startProcessing(cancellationToken).ConfigureAwait(false);
        _stoppedByHomeostat = false;
    }

    private void StartWatch()
    {
        lock (_gate)
        {
            if (_watchCancellation is not null)
            {
                return;
            }

            _watchCancellation = new CancellationTokenSource();
            var token = _watchCancellation.Token;
            _watch = Task.Run(() => WatchAsync(token), CancellationToken.None);
        }
    }

    /// <summary>
    /// While the pause stands, stops the processor whenever it is found processing (started by the application after
    /// the pause). Never throws; ends on resume or when the processor is closed.
    /// </summary>
    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_watchInterval, cancellationToken).ConfigureAwait(false);
                if (_isClosed())
                {
                    return;
                }

                if (_isProcessing())
                {
                    _stoppedByHomeostat = true;
                    using var timeout = new CancellationTokenSource(WatchStopTimeout);
                    await _stopProcessing(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // A stop that failed or timed out is tried again on the next look while the pause stands.
            }
        }
    }
}
