using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Homeostat.Consumers;
using Homeostat.Probes;
using Homeostat.ServiceBus;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat;

/// <summary>Azure Service Bus signals and pausable processors.</summary>
public static class HomeostatServiceBusExtensions
{
    /// <summary>
    /// Publishes the active and dead-letter message counts of a queue as two signals, <c>servicebus:{queue}:active</c>
    /// and <c>servicebus:{queue}:dead-letter</c>, with the values as counts.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="queue">Queue name.</param>
    /// <param name="configure">Thresholds, interval, source and client.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddSingleton(new ServiceBusAdministrationClient("ingest.servicebus.windows.net", new DefaultAzureCredential()));
    /// h.AddServiceBusQueueSignals("ingest", o => o.Active.UnhealthyAbove = 10_000)
    ///  .AddCondition("ingest-dead-letters", c => c.ForSource("servicebus:ingest:dead-letter")
    ///      .WhenRisingFasterThan(5, TimeSpan.FromMinutes(15)));
    /// </code>
    /// </example>
    public static HomeostatBuilder AddServiceBusQueueSignals(this HomeostatBuilder builder, string queue, Action<ServiceBusSignalOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        return AddSignals(builder, $"servicebus:{queue}", configure, async (client, ct) =>
        {
            var properties = (await client.GetQueueRuntimePropertiesAsync(queue, ct).ConfigureAwait(false)).Value;
            return new EntityCounts(properties.ActiveMessageCount, properties.DeadLetterMessageCount + properties.TransferDeadLetterMessageCount);
        });
    }

    /// <summary>
    /// Publishes the active and dead-letter message counts of a topic subscription as two signals,
    /// <c>servicebus:{topic}/{subscription}:active</c> and <c>servicebus:{topic}/{subscription}:dead-letter</c>.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="topic">Topic name.</param>
    /// <param name="subscription">Subscription name.</param>
    /// <param name="configure">Thresholds, interval, source and client.</param>
    public static HomeostatBuilder AddServiceBusSubscriptionSignals(
        this HomeostatBuilder builder, string topic, string subscription, Action<ServiceBusSignalOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscription);
        return AddSignals(builder, $"servicebus:{topic}/{subscription}", configure, async (client, ct) =>
        {
            var properties = (await client.GetSubscriptionRuntimePropertiesAsync(topic, subscription, ct).ConfigureAwait(false)).Value;
            return new EntityCounts(properties.ActiveMessageCount, properties.DeadLetterMessageCount + properties.TransferDeadLetterMessageCount);
        });
    }

    /// <summary>
    /// Lets plans pause and resume a <see cref="ServiceBusProcessor"/> registered in the services (see
    /// <c>PauseConsumer</c>). Pausing stops it receiving; messages being handled finish, and locked messages are
    /// released when their lock expires.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="name">Consumer name, used by <c>PauseConsumer(name)</c> and the switch <c>consumer:{name}:paused</c>.</param>
    /// <param name="processor">Returns the processor; by default the <see cref="ServiceBusProcessor"/> registered in the services.</param>
    public static HomeostatBuilder AddPausableServiceBusProcessor(
        this HomeostatBuilder builder, string name, Func<IServiceProvider, ServiceBusProcessor>? processor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var resolve = processor ?? (sp => sp.GetRequiredService<ServiceBusProcessor>());
        return builder.AddPausableConsumer(sp => new PausableServiceBusProcessor(name, resolve(sp)));
    }

    /// <summary>Lets plans pause and resume a <see cref="ServiceBusSessionProcessor"/> (see <see cref="AddPausableServiceBusProcessor"/>).</summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="name">Consumer name.</param>
    /// <param name="processor">Returns the session processor.</param>
    public static HomeostatBuilder AddPausableServiceBusSessionProcessor(
        this HomeostatBuilder builder, string name, Func<IServiceProvider, ServiceBusSessionProcessor> processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(processor);
        return builder.AddPausableConsumer(sp => new PausableServiceBusProcessor(name, processor(sp)));
    }

    private static HomeostatBuilder AddSignals(
        HomeostatBuilder builder,
        string defaultSource,
        Action<ServiceBusSignalOptions>? configure,
        Func<ServiceBusAdministrationClient, CancellationToken, Task<EntityCounts>> read)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new ServiceBusSignalOptions();
        configure?.Invoke(options);
        var client = options.Client ?? (sp => sp.GetRequiredService<ServiceBusAdministrationClient>());
        return AddCountSignals(builder, options.Source ?? defaultSource, options, (sp, ct) => read(client(sp), ct));
    }

    /// <summary>One read of the counts feeds both probes, so the entity is read once per interval.</summary>
    internal static HomeostatBuilder AddCountSignals(
        HomeostatBuilder builder, string source, ServiceBusSignalOptions options, Func<IServiceProvider, CancellationToken, Task<EntityCounts>> read)
    {
        if (options.Interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The interval must be positive.");
        }

        // The shared read gets its own deadline, the longest of the two probes; each probe stops waiting at its own.
        var cache = new SharedRead<EntityCounts>(read, Max(options.Active.Timeout, options.DeadLetter.Timeout));
        void Settings(ProbeOptions from, ProbeOptions to)
        {
            from.CopyTo(to);
            to.Interval ??= options.Interval;
            to.Origin ??= "servicebus";
        }

        return builder
            .AddProbe($"{source}:active", async (sp, ct) => (await cache.GetAsync(sp).WaitAsync(ct).ConfigureAwait(false)).Active, p => Settings(options.Active, p))
            .AddProbe($"{source}:dead-letter", async (sp, ct) => (await cache.GetAsync(sp).WaitAsync(ct).ConfigureAwait(false)).DeadLetter, p => Settings(options.DeadLetter, p));
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

/// <summary>Message counts of a queue or subscription.</summary>
internal readonly record struct EntityCounts(long Active, long DeadLetter);

/// <summary>
/// Shares one read between the probes of an entity that run at the same time: a probe gets the result of the read
/// in progress, or of one completed less than a second ago. The read has its own deadline, so one probe's timeout or
/// cancellation never fails the other.
/// </summary>
internal sealed class SharedRead<T>(Func<IServiceProvider, CancellationToken, Task<T>> read, TimeSpan timeout)
{
    private readonly object _gate = new();
    private Task<T>? _current;
    private long _startedAt;

    public Task<T> GetAsync(IServiceProvider services)
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

    private async Task<T> ReadAsync(IServiceProvider services)
    {
        using var deadline = new CancellationTokenSource(timeout);
        return await read(services, deadline.Token).ConfigureAwait(false);
    }
}
