using Confluent.Kafka;
using Homeostat.Kafka;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat;

/// <summary>Kafka consumer lag as a Homeostat signal.</summary>
public static class HomeostatKafkaExtensions
{
    /// <summary>
    /// Publishes the consumer lag as the signal <c>kafka:{name}:lag</c> from the client statistics. Set
    /// <c>StatisticsIntervalMs</c> in the consumer configuration (for example 30 000): without it the client emits no
    /// statistics and no signal is published. A consumer has a single statistics handler; to keep your own, call
    /// <see cref="KafkaLagReporter.Report"/> from it instead.
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
    ///     .SetHomeostatLagSignal(sp, "telemetry", o => o.UnhealthyAbove = 100_000)
    ///     .Build();
    /// </code>
    /// </example>
    public static ConsumerBuilder<TKey, TValue> SetHomeostatLagSignal<TKey, TValue>(
        this ConsumerBuilder<TKey, TValue> builder, IServiceProvider services, string name, Action<KafkaLagOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(services);
        var options = new KafkaLagOptions();
        configure?.Invoke(options);
        var reporter = new KafkaLagReporter(
            services.GetRequiredService<ISignalPublisher>(), name, options, services.GetService<TimeProvider>());
        return builder.SetStatisticsHandler((_, json) => reporter.Report(json));
    }
}
