using Homeostat.Probes;
using Homeostat.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Homeostat;

/// <summary>RabbitMQ queue signals: messages ready, consumers and dead letters, read from the management of the broker.</summary>
public static class HomeostatRabbitMqExtensions
{
    /// <summary>
    /// Publishes the ready message count and the consumer count of a queue, <c>rabbitmq:{queue}:messages</c> and
    /// <c>rabbitmq:{queue}:consumers</c>, and optionally the message count of its dead-letter queue. The queue is read
    /// with a passive declare on a short-lived channel of the application's connection; a missing queue makes the probe
    /// fail (Unhealthy, without a value).
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="queue">Queue name.</param>
    /// <param name="configure">Thresholds, interval, dead-letter queue, source and connection.</param>
    /// <example>
    /// <code>
    /// h.AddRabbitMqQueueSignals("thumbnails", o =>
    /// {
    ///     o.Messages.UnhealthyAbove = 50_000;
    ///     o.DeadLetterQueue = "thumbnails.dead-letter";
    /// });
    /// </code>
    /// </example>
    public static HomeostatBuilder AddRabbitMqQueueSignals(this HomeostatBuilder builder, string queue, Action<RabbitMqQueueSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        var options = new RabbitMqQueueSignalOptions();
        configure?.Invoke(options);
        if (options.Interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(configure), "The interval must be positive.");
        }

        var connection = options.Connection ?? (sp => sp.GetRequiredService<IConnection>());
        return AddQueueSignals(builder, queue, options, (sp, name, ct) => ReadAsync(connection(sp), name, ct));
    }

    internal static HomeostatBuilder AddQueueSignals(
        HomeostatBuilder builder, string queue, RabbitMqQueueSignalOptions options, Func<IServiceProvider, string, CancellationToken, Task<QueueCounts>> read)
    {
        var source = options.Source ?? $"rabbitmq:{queue}";
        // The shared read gets its own deadline, the longest of the two probes; each probe stops waiting at its own.
        var shared = new SharedQueueRead((sp, ct) => read(sp, queue, ct), options.Messages.Timeout > options.Consumers.Timeout ? options.Messages.Timeout : options.Consumers.Timeout);
        void Settings(ProbeOptions from, ProbeOptions to)
        {
            from.CopyTo(to);
            to.Interval ??= options.Interval;
            to.Origin ??= "rabbitmq";
        }

        builder
            .AddProbe($"{source}:messages", async (sp, ct) => (await shared.GetAsync(sp).WaitAsync(ct).ConfigureAwait(false)).Messages, p => Settings(options.Messages, p))
            .AddProbe($"{source}:consumers", async (sp, ct) => (await shared.GetAsync(sp).WaitAsync(ct).ConfigureAwait(false)).Consumers, p => Settings(options.Consumers, p));

        if (options.DeadLetterQueue is { Length: > 0 } deadLetter)
        {
            builder.AddProbe($"{source}:dead-letter", async (sp, ct) => (await read(sp, deadLetter, ct).ConfigureAwait(false)).Messages, p => Settings(options.DeadLetter, p));
        }

        return builder;
    }

    /// <summary>A passive declare on its own channel: a missing queue closes the channel, never the application's.</summary>
    private static async Task<QueueCounts> ReadAsync(IConnection connection, string queue, CancellationToken cancellationToken)
    {
        var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (channel.ConfigureAwait(false))
        {
            var ok = await channel.QueueDeclarePassiveAsync(queue, cancellationToken).ConfigureAwait(false);
            return new QueueCounts(ok.MessageCount, ok.ConsumerCount);
        }
    }
}

/// <summary>Counts of a queue.</summary>
internal readonly record struct QueueCounts(long Messages, long Consumers);

/// <summary>Shares one read between the probes of a queue that run together, with its own deadline.</summary>
internal sealed class SharedQueueRead(Func<IServiceProvider, CancellationToken, Task<QueueCounts>> read, TimeSpan timeout)
{
    private readonly object _gate = new();
    private Task<QueueCounts>? _current;
    private long _startedAt;

    public Task<QueueCounts> GetAsync(IServiceProvider services)
    {
        lock (_gate)
        {
            // A read in progress, or one (even failed) from less than a second ago, serves every probe of the tick.
            if (_current is { } current && (!current.IsCompleted || Environment.TickCount64 - _startedAt < 1000))
            {
                return current;
            }

            _startedAt = Environment.TickCount64;
            _current = ReadAsync(services);

            // Probes that stopped waiting (their own timeout) leave the failure unobserved: observe it here.
            _current.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return _current;
        }
    }

    private async Task<QueueCounts> ReadAsync(IServiceProvider services)
    {
        using var deadline = new CancellationTokenSource(timeout);
        return await read(services, deadline.Token).ConfigureAwait(false);
    }
}
