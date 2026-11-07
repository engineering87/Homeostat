using Azure.Messaging.ServiceBus;
using Homeostat.ServiceBus;

namespace Homeostat.IntegrationTests;

/// <summary>
/// Against the Service Bus emulator: HOMEOSTAT_TEST_SERVICEBUS is its connection string, and the queue "ingest" exists
/// (see the CI workflow's emulator configuration).
/// </summary>
public class ServiceBusTests
{
    [Fact]
    public async Task A_paused_processor_receives_nothing_until_it_is_resumed()
    {
        var connection = Support.Variable("HOMEOSTAT_TEST_SERVICEBUS");
        await using var client = new ServiceBusClient(connection);
        await using var sender = client.CreateSender("ingest");
        await using var processor = client.CreateProcessor("ingest", new ServiceBusProcessorOptions { MaxConcurrentCalls = 1 });
        var received = 0;
        processor.ProcessMessageAsync += async args =>
        {
            Interlocked.Increment(ref received);
            await args.CompleteMessageAsync(args.Message);
        };
        processor.ProcessErrorAsync += _ => Task.CompletedTask;
        await processor.StartProcessingAsync();
        var pausable = new PausableServiceBusProcessor("ingest", processor);

        await sender.SendMessageAsync(new ServiceBusMessage("document-1"));
        await Support.EventuallyAsync(() => Volatile.Read(ref received) == 1, what: "the first message");

        // Paused: a message sent now waits in the queue.
        await pausable.PauseAsync(CancellationToken.None);
        Assert.False(processor.IsProcessing);
        await sender.SendMessageAsync(new ServiceBusMessage("document-2"));
        await Task.Delay(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref received));

        // Resumed: it arrives.
        await pausable.ResumeAsync(CancellationToken.None);
        await Support.EventuallyAsync(() => Volatile.Read(ref received) == 2, what: "the message sent while paused");
    }

    [Fact]
    public async Task A_processor_the_application_stopped_is_not_started_by_a_resume()
    {
        var connection = Support.Variable("HOMEOSTAT_TEST_SERVICEBUS");
        await using var client = new ServiceBusClient(connection);
        await using var processor = client.CreateProcessor("ingest");
        processor.ProcessMessageAsync += args => args.AbandonMessageAsync(args.Message);
        processor.ProcessErrorAsync += _ => Task.CompletedTask;
        var pausable = new PausableServiceBusProcessor("ingest", processor);

        await pausable.PauseAsync(CancellationToken.None);    // not processing: nothing to do
        await pausable.ResumeAsync(CancellationToken.None);   // the application never started it: it stays stopped

        Assert.False(processor.IsProcessing);
    }
}
