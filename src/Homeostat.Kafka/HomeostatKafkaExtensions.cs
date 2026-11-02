using Confluent.Kafka;
using Homeostat.Kafka;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat;

/// <summary>Kafka client statistics as Homeostat signals.</summary>
public static class HomeostatKafkaExtensions
{
    /// <summary>
    /// Publishes the consumer's broker connections and lag, <c>kafka:{name}:brokers</c> and <c>kafka:{name}:lag</c>, from
    /// the client statistics. Set <c>StatisticsIntervalMs</c> in the consumer configuration (for example 30 000): without
    /// it the client emits no statistics and no signal is published. A client has a single statistics handler; to keep
    /// your own, call <see cref="KafkaSignalReporter.Report"/> from it instead.
    /// </summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <typeparam name="TValue">Value type.</typeparam>
    /// <param name="builder">The consumer builder.</param>
    /// <param name="services">The application's services, for the signal publisher.</param>
    /// <param name="name">Consumer name.</param>
    /// <param name="configure">Thresholds and source.</param>
    /// <example>
    /// <code>
    /// var consumer = new ConsumerBuilder&lt;string, byte[]&gt;(new ConsumerConfig { GroupId = "telemetry", StatisticsIntervalMs = 30_000, ... })
    ///     .SetHomeostatSignals(sp, "telemetry", o => o.LagUnhealthyAbove = 100_000)
    ///     .Build();
    /// </code>
    /// </example>
    public static ConsumerBuilder<TKey, TValue> SetHomeostatSignals<TKey, TValue>(
        this ConsumerBuilder<TKey, TValue> builder, IServiceProvider services, string name, Action<KafkaSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var reporter = Reporter(services, name, configure);
        return builder.SetStatisticsHandler((_, json) => reporter.Report(json));
    }

    /// <summary>
    /// Publishes the producer's broker connections and local queue use, <c>kafka:{name}:brokers</c> and
    /// <c>kafka:{name}:queue</c>, from the client statistics (set <c>StatisticsIntervalMs</c>). A producer whose brokers
    /// are down keeps accepting messages until its local queue is full: the queue signal shows it coming.
    /// </summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <typeparam name="TValue">Value type.</typeparam>
    /// <param name="builder">The producer builder.</param>
    /// <param name="services">The application's services, for the signal publisher.</param>
    /// <param name="name">Producer name.</param>
    /// <param name="configure">Thresholds and source.</param>
    public static ProducerBuilder<TKey, TValue> SetHomeostatSignals<TKey, TValue>(
        this ProducerBuilder<TKey, TValue> builder, IServiceProvider services, string name, Action<KafkaSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var reporter = Reporter(services, name, configure);
        return builder.SetStatisticsHandler((_, json) => reporter.Report(json));
    }

    private static KafkaSignalReporter Reporter(IServiceProvider services, string name, Action<KafkaSignalOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new KafkaSignalOptions();
        configure?.Invoke(options);
        return new KafkaSignalReporter(services.GetRequiredService<ISignalPublisher>(), name, options, services.GetService<TimeProvider>());
    }
}
