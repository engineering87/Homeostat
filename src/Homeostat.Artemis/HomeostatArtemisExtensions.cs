using Homeostat.Artemis;
using Homeostat.Probes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Homeostat;

/// <summary>ActiveMQ Artemis signals, read through the broker's Jolokia management API.</summary>
public static class HomeostatArtemisExtensions
{
    /// <summary>
    /// Configures the broker whose management API the Artemis signals read. Call it once, then
    /// <see cref="AddArtemisQueueSignals"/> for each queue.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Management address, broker name, credentials and origin.</param>
    /// <example>
    /// <code>
    /// h.UseArtemis(o =>
    /// {
    ///     o.ManagementUrl = new Uri("http://artemis:8161");
    ///     o.BrokerName = "0.0.0.0";
    ///     o.UserName = "monitor";
    ///     o.Password = builder.Configuration["Artemis:MonitorPassword"];
    /// })
    /// .AddArtemisQueueSignals("telemetry-ingest", q => { q.Messages.UnhealthyAbove = 20_000; q.DeadLetterQueue = "DLQ"; });
    /// </code>
    /// </example>
    public static HomeostatBuilder UseArtemis(this HomeostatBuilder builder, Action<ArtemisOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.AddOptions<ArtemisOptions>().Configure(configure).Validate(o =>
        {
            o.Validate();
            return true;
        }).ValidateOnStart();
        builder.Services.AddHttpClient(JolokiaClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.TryAddSingleton(sp => new JolokiaClient(
            sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IOptions<ArtemisOptions>>().Value));
        return builder;
    }

    /// <summary>
    /// Publishes the message count and the consumer count of a queue, <c>artemis:{queue}:messages</c> and
    /// <c>artemis:{queue}:consumers</c>, read in one request per interval, and optionally the message count of a
    /// dead-letter queue (a second request). A queue that does not exist makes the probe fail (Unhealthy, without a value).
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="queue">Queue name.</param>
    /// <param name="configure">Address, routing type, thresholds, interval, dead-letter queue and source.</param>
    public static HomeostatBuilder AddArtemisQueueSignals(this HomeostatBuilder builder, string queue, Action<ArtemisQueueSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        var options = new ArtemisQueueSignalOptions();
        configure?.Invoke(options);
        if (options.Interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(configure), "The interval must be positive.");
        }

        if (options.RoutingType is not ("anycast" or "multicast"))
        {
            throw new ArgumentException("RoutingType must be anycast or multicast.", nameof(configure));
        }

        return AddQueueSignals(builder, queue, options, (sp, mbeanQueue, address, routing, ct) =>
        {
            var client = sp.GetService<JolokiaClient>()
                ?? throw new InvalidOperationException("Call UseArtemis(o => ...) to set the broker's management address.");
            var broker = sp.GetRequiredService<IOptions<ArtemisOptions>>().Value.BrokerName!;
            return client.ReadAsync(JolokiaClient.QueueMBean(broker, address, routing, mbeanQueue), QueueAttributes, ct);
        });
    }

    private static readonly string[] QueueAttributes = ["MessageCount", "ConsumerCount"];

    internal delegate Task<IReadOnlyDictionary<string, double>> QueueReader(
        IServiceProvider services, string queue, string address, string routingType, CancellationToken cancellationToken);

    internal static HomeostatBuilder AddQueueSignals(HomeostatBuilder builder, string queue, ArtemisQueueSignalOptions options, QueueReader read)
    {
        var source = options.Source ?? $"artemis:{queue}";
        var address = options.Address ?? queue;
        var timeout = options.Messages.Timeout > options.Consumers.Timeout ? options.Messages.Timeout : options.Consumers.Timeout;
        var shared = new SharedRead((sp, ct) => read(sp, queue, address, options.RoutingType, ct), timeout);
        void Settings(ProbeOptions from, ProbeOptions to)
        {
            from.CopyTo(to);
            to.Interval ??= options.Interval;
            to.Origin ??= "artemis";
        }

        builder
            .AddProbe($"{source}:messages", async (sp, ct) => (await shared.GetAsync(sp).WaitAsync(ct).ConfigureAwait(false))["MessageCount"], p => Settings(options.Messages, p))
            .AddProbe($"{source}:consumers", async (sp, ct) => (await shared.GetAsync(sp).WaitAsync(ct).ConfigureAwait(false))["ConsumerCount"], p => Settings(options.Consumers, p));

        if (options.DeadLetterQueue is { Length: > 0 } deadLetter)
        {
            builder.AddProbe($"{source}:dead-letter", async (sp, ct) => (await read(sp, deadLetter, options.DeadLetterAddress, "anycast", ct).ConfigureAwait(false))["MessageCount"],
                p => Settings(options.DeadLetter, p));
        }

        return builder;
    }
}

/// <summary>Shares one read between the probes of a queue that run together, with its own deadline.</summary>
internal sealed class SharedRead(Func<IServiceProvider, CancellationToken, Task<IReadOnlyDictionary<string, double>>> read, TimeSpan timeout)
{
    private readonly object _gate = new();
    private Task<IReadOnlyDictionary<string, double>>? _current;
    private long _startedAt;

    public Task<IReadOnlyDictionary<string, double>> GetAsync(IServiceProvider services)
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

    private async Task<IReadOnlyDictionary<string, double>> ReadAsync(IServiceProvider services)
    {
        using var deadline = new CancellationTokenSource(timeout);
        return await read(services, deadline.Token).ConfigureAwait(false);
    }
}
