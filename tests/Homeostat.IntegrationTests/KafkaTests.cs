using Confluent.Kafka;
using Homeostat.Kafka;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.IntegrationTests;

/// <summary>Against a real Kafka broker: HOMEOSTAT_TEST_KAFKA is its bootstrap address (for example localhost:9092).</summary>
public class KafkaTests
{
    private static async Task<string> TopicWithOneMessageAsync(string servers)
    {
        var topic = Support.Unique("homeostat-it");
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = servers }).Build();
        await producer.ProduceAsync(topic, new Message<string, string> { Key = "station-1", Value = "reading" });   // creates the topic
        producer.Flush(TimeSpan.FromSeconds(10));
        return topic;
    }

    [Fact]
    public async Task Client_statistics_become_signals()
    {
        var servers = Support.Variable("HOMEOSTAT_TEST_KAFKA");
        var signals = new CollectingPublisher();
        await using var services = new ServiceCollection().AddSingleton<ISignalPublisher>(signals).BuildServiceProvider();

        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = servers, StatisticsIntervalMs = 500 })
            .SetHomeostatSignals(services, "telemetry")
            .Build();
        await producer.ProduceAsync(Support.Unique("homeostat-it"), new Message<string, string> { Key = "station-1", Value = "reading" });

        await Support.EventuallyAsync(() => signals.Latest("kafka:telemetry:brokers") is not null, what: "the brokers signal");
        var brokers = signals.Latest("kafka:telemetry:brokers")!;
        Assert.Equal(SignalSeverity.Healthy, brokers.Severity);
        Assert.True(brokers.Value >= 1, $"brokers up: {brokers.Value}");
    }

    [Fact]
    public async Task The_pause_gate_pauses_and_resumes_a_real_consumer()
    {
        var servers = Support.Variable("HOMEOSTAT_TEST_KAFKA");
        var topic = await TopicWithOneMessageAsync(servers);
        var gate = new KafkaPauseGate("telemetry");

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = servers,
            GroupId = Support.Unique("homeostat-it"),
            EnableAutoCommit = false,
        }).Build();
        consumer.Assign(new TopicPartitionOffset(topic, 0, Offset.Beginning));

        // Paused: the message waiting in the partition is not delivered.
        await gate.PauseAsync(CancellationToken.None);
        for (var i = 0; i < 6; i++)
        {
            gate.Apply(consumer);
            Assert.Null(consumer.Consume(TimeSpan.FromMilliseconds(500)));
        }

        // Resumed: it is.
        await gate.ResumeAsync(CancellationToken.None);
        ConsumeResult<string, string>? delivered = null;
        for (var i = 0; i < 40 && delivered is null; i++)
        {
            gate.Apply(consumer);
            delivered = consumer.Consume(TimeSpan.FromMilliseconds(500));
        }

        Assert.NotNull(delivered);
        Assert.Equal("reading", delivered!.Message.Value);
    }
}
