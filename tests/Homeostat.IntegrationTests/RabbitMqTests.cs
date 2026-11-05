using System.Text;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Homeostat.IntegrationTests;

/// <summary>Against a real RabbitMQ broker: HOMEOSTAT_TEST_RABBITMQ is its AMQP address (amqp://guest:guest@localhost:5672).</summary>
public class RabbitMqTests
{
    [Fact]
    public async Task Queue_signals_count_the_messages_and_the_consumers()
    {
        var address = Support.Variable("HOMEOSTAT_TEST_RABBITMQ");
        var queue = Support.Unique("homeostat-it");
        var factory = new ConnectionFactory { Uri = new Uri(address) };
        await using var connection = await factory.CreateConnectionAsync();
        await using (var channel = await connection.CreateChannelAsync())
        {
            await channel.QueueDeclareAsync(queue: queue, durable: false, exclusive: false, autoDelete: false);
            await channel.BasicPublishAsync(exchange: string.Empty, routingKey: queue, body: Encoding.UTF8.GetBytes("thumbnail-1"));
            await channel.BasicPublishAsync(exchange: string.Empty, routingKey: queue, body: Encoding.UTF8.GetBytes("thumbnail-2"));
        }

        var signals = new CollectingPublisher();
        var collection = new ServiceCollection().AddLogging();
        collection.AddHomeostat(h => h.AddRabbitMqQueueSignals(queue, o =>
        {
            o.Connection = _ => connection;
            o.Interval = TimeSpan.FromSeconds(1);
        }));
        await using var services = collection.BuildServiceProvider();

        try
        {
            await Support.EventuallyAsync(
                () => signals.Latest($"rabbitmq:{queue}:messages")?.Value == 2 && signals.Latest($"rabbitmq:{queue}:consumers")?.Value == 0,
                () => Support.DetectAsync(services, signals),
                what: "the queue signals");
        }
        finally
        {
            await using var cleanup = await connection.CreateChannelAsync();
            await cleanup.QueueDeleteAsync(queue);
        }
    }
}
