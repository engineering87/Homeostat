using Homeostat.Consumers;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Homeostat.RabbitMQ;

/// <summary>
/// Consumes a queue with a RabbitMQ consumer that Homeostat can pause and resume: pausing cancels the subscription
/// (the broker stops delivering; messages already delivered are still handled), resuming subscribes again. Use it
/// instead of calling <c>BasicConsumeAsync</c> yourself.
/// </summary>
/// <example>
/// <code>
/// var consumers = sp.GetRequiredService&lt;IHomeostatConsumers&gt;();
/// var consumer = new PausableRabbitMqConsumer("thumbnails", channel, "thumbnails", autoAck: false, new ThumbnailConsumer(channel));
/// using var registration = consumers.Register(consumer);
/// if (consumers.IsPaused(consumer.Name)) await consumer.PauseAsync(ct);
/// await consumer.StartAsync(ct);
/// </code>
/// </example>
public sealed class PausableRabbitMqConsumer : IPausableConsumer
{
    private readonly IChannel _channel;
    private readonly string _queue;
    private readonly bool _autoAck;
    private readonly IAsyncBasicConsumer _consumer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _consumerTag;
    private bool _pauseRequested;

    /// <summary>Prepares the consumer; call <see cref="StartAsync"/> to start consuming.</summary>
    /// <param name="name">Consumer name, used by <c>PauseConsumer(name)</c>.</param>
    /// <param name="channel">Channel to consume on.</param>
    /// <param name="queue">Queue to consume.</param>
    /// <param name="autoAck">Whether the broker considers messages acknowledged when delivered.</param>
    /// <param name="consumer">Handles the deliveries.</param>
    public PausableRabbitMqConsumer(string name, IChannel channel, string queue, bool autoAck, IAsyncBasicConsumer consumer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        ArgumentNullException.ThrowIfNull(consumer);
        Name = name;
        _channel = channel;
        _queue = queue;
        _autoAck = autoAck;
        _consumer = new TrackingConsumer(consumer, this);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Whether the consumer is subscribed to the queue.</summary>
    public bool IsConsuming => Volatile.Read(ref _consumerTag) is not null;

    /// <summary>
    /// Starts consuming, unless the consumer was paused before (by Homeostat, or by you because
    /// <c>IHomeostatConsumers.IsPaused</c> said so right after registering it): then it starts when resumed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_pauseRequested)
            {
                await SubscribeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Cancels the subscription.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pauseRequested = true;
            if (Volatile.Read(ref _consumerTag) is { } tag)
            {
                await CancelAsync(tag, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Subscribes again.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A tag still held after a pause means the cancel failed on an open channel (a timeout while the broker
            // was slow, under a resource alarm): the broker may have applied it all the same and discarded the late
            // cancel-ok, leaving the queue without a consumer while the tag says otherwise. Cancel again (the broker
            // answers cancel-ok for a tag it no longer knows), then subscribe. If that cancel fails too, the resume
            // fails and stays paused, to be tried again.
            if (_pauseRequested && Volatile.Read(ref _consumerTag) is { } stale)
            {
                await CancelAsync(stale, cancellationToken).ConfigureAwait(false);
            }

            _pauseRequested = false;
            await SubscribeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller holds the gate.
    private async Task CancelAsync(string tag, CancellationToken cancellationToken)
    {
        try
        {
            await _channel.BasicCancelAsync(tag, noWait: false, cancellationToken).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _consumerTag, null, tag);
        }
        catch when (!_channel.IsOpen)
        {
            // The channel is gone: the subscription with it (and automatic recovery no longer restores a
            // consumer whose cancellation was requested). Nothing left to cancel.
            Interlocked.CompareExchange(ref _consumerTag, null, tag);
        }
    }

    // Caller holds the gate.
    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _consumerTag) is null)
        {
            var tag = await _channel.BasicConsumeAsync(_queue, _autoAck, _consumer, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _consumerTag, tag);
        }
    }

    /// <summary>
    /// The subscription ended: the broker canceled it (queue deleted, node failure) or confirmed a cancel. Resuming
    /// must subscribe again.
    /// </summary>
    private void OnCanceled(string consumerTag)
    {
        // Tags are compared by value: the broker's string is never the instance we stored.
        if (Volatile.Read(ref _consumerTag) is { } current && string.Equals(current, consumerTag, StringComparison.Ordinal))
        {
            Interlocked.CompareExchange(ref _consumerTag, null, current);
        }
    }

    /// <summary>Forwards everything to the application's consumer, and notices when the broker cancels it.</summary>
    private sealed class TrackingConsumer(IAsyncBasicConsumer inner, PausableRabbitMqConsumer owner) : IAsyncBasicConsumer
    {
        public IChannel? Channel => inner.Channel;

        public async Task HandleBasicCancelAsync(string consumerTag, CancellationToken cancellationToken = default)
        {
            owner.OnCanceled(consumerTag);
            await inner.HandleBasicCancelAsync(consumerTag, cancellationToken).ConfigureAwait(false);
        }

        public async Task HandleBasicCancelOkAsync(string consumerTag, CancellationToken cancellationToken = default)
        {
            // A confirmed cancel ends the subscription whoever asked for it, including a second cancel sent after one
            // that timed out.
            owner.OnCanceled(consumerTag);
            await inner.HandleBasicCancelOkAsync(consumerTag, cancellationToken).ConfigureAwait(false);
        }

        public Task HandleBasicConsumeOkAsync(string consumerTag, CancellationToken cancellationToken = default)
            => inner.HandleBasicConsumeOkAsync(consumerTag, cancellationToken);

        public Task HandleBasicDeliverAsync(string consumerTag, ulong deliveryTag, bool redelivered, string exchange, string routingKey,
            IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body, CancellationToken cancellationToken = default)
            => inner.HandleBasicDeliverAsync(consumerTag, deliveryTag, redelivered, exchange, routingKey, properties, body, cancellationToken);

        public Task HandleChannelShutdownAsync(object channel, ShutdownEventArgs reason)
            => inner.HandleChannelShutdownAsync(channel, reason);
    }
}
