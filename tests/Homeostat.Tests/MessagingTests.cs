using System.Diagnostics.Tracing;
using System.Reflection;
using Aspire.Hosting;
using Azure.Messaging.ServiceBus;
using Confluent.Kafka;
using Homeostat.Consumers;
using Homeostat.Kafka;
using Homeostat.Metrics;
using Homeostat.RabbitMQ;
using Homeostat.ServiceBus;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Homeostat.Tests;

public class MessagingTests
{
    // ---------------------------------------------------------------- pausable consumers

    private sealed class FakeConsumer(string name) : IPausableConsumer
    {
        private readonly object _gate = new();
        private readonly List<string> _calls = [];

        public string Name { get; } = name;

        public IReadOnlyList<string> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        public Task PauseAsync(CancellationToken cancellationToken) => Record("pause");

        public Task ResumeAsync(CancellationToken cancellationToken) => Record("resume");

        private Task Record(string call)
        {
            lock (_gate)
            {
                _calls.Add(call);
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_plan_pauses_a_consumer_and_resumes_it_when_the_condition_clears()
    {
        var consumer = new FakeConsumer("ingest");
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddPausableConsumer(_ => consumer)
            .AddCondition("archive-down", c => c.ForSource("archive-db").Heal(p => p
                .PauseConsumer("ingest")
                .VerifyWith<BuiltInActionTests.AlwaysHealed>())));
        var consumers = host.Services.GetRequiredService<IHomeostatConsumers>();

        host.Publish("archive-db", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("archive-down");
        await PollyControlTests.WaitUntilAsync(() => consumer.Calls.Count == 1);
        Assert.True(consumers.IsPaused("ingest"));
        Assert.Equal(["ingest"], consumers.Names);

        host.Publish("archive-db", SignalSeverity.Healthy);
        await PollyControlTests.WaitUntilAsync(() => consumer.Calls.Count == 2);
        Assert.Equal(["pause", "resume"], consumer.Calls);
        Assert.False(consumers.IsPaused("ingest"));
    }

    [Fact]
    public async Task Consumers_registered_later_follow_their_switch()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddCondition("x", c => c.ForSource("x")));
        var switches = host.Services.GetRequiredService<IHomeostatSwitches>();
        var consumers = (HomeostatConsumers)host.Services.GetRequiredService<IHomeostatConsumers>();

        switches.TurnOn(ConsumerSwitches.Paused("telemetry"));
        var first = new FakeConsumer("telemetry");
        var second = new FakeConsumer("telemetry");
        var registration = consumers.Register(first);
        using var _ = consumers.Register(second);
        await consumers.WhenAppliedAsync();
        Assert.Equal(["pause"], first.Calls);                              // paused at registration
        Assert.Throws<InvalidOperationException>(() => consumers.Register(first));

        registration.Dispose();
        switches.TurnOff(ConsumerSwitches.Paused("telemetry"));            // by hand, from the dashboard or the CLI
        await consumers.WhenAppliedAsync();
        Assert.Equal(["pause"], first.Calls);                              // no longer registered
        Assert.Equal(["pause", "resume"], second.Calls);

        switches.TurnOff(ConsumerSwitches.Paused("telemetry"));
        await consumers.WhenAppliedAsync();
        Assert.Equal(["pause", "resume"], second.Calls);                   // already running: nothing to do
    }

    private sealed class FlakyConsumer(string name) : IPausableConsumer
    {
        public int Failures { get; set; } = 1;

        public int Pauses { get; private set; }

        public string Name { get; } = name;

        public Task PauseAsync(CancellationToken cancellationToken)
        {
            if (Failures-- > 0)
            {
                throw new InvalidOperationException("processor busy");
            }

            Pauses++;
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_failed_pause_is_retried_until_it_succeeds()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddCondition("x", c => c.ForSource("x")));
        var consumers = host.Services.GetRequiredService<IHomeostatConsumers>();
        var consumer = new FlakyConsumer("ingest");
        using var _ = consumers.Register(consumer);

        host.Services.GetRequiredService<IHomeostatSwitches>().TurnOn(ConsumerSwitches.Paused("ingest"));
        await PollyControlTests.WaitUntilAsync(() =>
        {
            host.Time.Advance(TimeSpan.FromSeconds(6));                    // past the first retry delay
            return consumer.Pauses == 1;
        });
    }

    [Fact]
    public async Task A_switch_turned_on_by_hand_is_not_turned_off_by_a_condition()
    {
        var consumer = new FakeConsumer("ingest");
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddPausableConsumer(_ => consumer)
            .AddCondition("archive-down", c => c.ForSource("archive-db").Heal(p => p
                .PauseConsumer("ingest")
                .VerifyWith<BuiltInActionTests.AlwaysHealed>())));
        var switches = host.Services.GetRequiredService<IHomeostatSwitches>();

        switches.TurnOn(ConsumerSwitches.Paused("ingest"));                // maintenance, from the dashboard
        host.Publish("archive-db", SignalSeverity.Unhealthy);
        await host.WaitForRunAsync("archive-down");
        host.Publish("archive-db", SignalSeverity.Healthy);
        await host.WaitForConditionAsync("archive-down", Conditions.ConditionStatus.Inactive);

        Assert.True(switches.IsOn(ConsumerSwitches.Paused("ingest")));
    }

    // ---------------------------------------------------------------- event counters

    [EventSource(Name = "Homeostat-Tests-Pool")]
    private sealed class PoolEventSource : EventSource
    {
        public double Active { get; set; }

        private PollingCounter? _counter;

        public PoolEventSource() => _counter = new PollingCounter("active-connections", this, () => Active);

        protected override void Dispose(bool disposing)
        {
            _counter?.Dispose();
            _counter = null;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task Event_counters_become_signals_without_enabling_tracing()
    {
        using var source = new PoolEventSource { Active = 90 };
        var signals = new CollectingPublisher();
        var registration = new EventCounterRegistration("Homeostat-Tests-Pool", "active-connections", "pool",
            new EventCounterSignalOptions { IntervalSeconds = 1, Transform = v => v / 100, DegradedAbove = 0.8, UnhealthyAbove = 0.95 });

        using (new EventCounterSignalListener([registration], signals, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance))
        {
            await PollyControlTests.WaitUntilAsync(() => signals.Signals.Count > 0);
            Assert.False(source.IsEnabled(EventLevel.Informational, EventKeywords.All));   // the library's own events stay off
        }

        var signal = signals.Signals[0];
        Assert.Equal(("pool", SignalSeverity.Degraded), (signal.Source, signal.Severity));
        Assert.Equal(0.9, signal.Value!.Value, precision: 6);
    }

    [Fact]
    public void SqlClient_pool_signal_reads_the_active_connections_as_a_share_of_the_pool()
    {
        var services = new ServiceCollection();
        services.AddHomeostat(h => h.AddSqlClientPoolSignal(maxPoolSize: 200));

        var registration = Assert.Single(services.Select(d => d.ImplementationInstance).OfType<EventCounterRegistration>());
        Assert.Equal(("Microsoft.Data.SqlClient.EventSource", "number-of-active-connections", "sqlclient-pool"),
            (registration.EventSource, registration.Counter, registration.Source));
        Assert.Equal(0.5, registration.Options.Transform!(100));
        Assert.Equal(SignalSeverity.Unhealthy, registration.Options.SeverityOf(0.96));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddHomeostat(h => h.AddEventCounterSignal("s", "c", o => o.IntervalSeconds = 0)));
    }

    // ---------------------------------------------------------------- Service Bus

    [Fact]
    public async Task Service_Bus_counts_become_two_signals_from_one_read()
    {
        var reads = 0;
        var (services, detector) = ProbeTests.Build(h =>
        {
            var options = new ServiceBusSignalOptions();
            options.Active.UnhealthyAbove = 1_000;
            HomeostatServiceBusExtensions.AddCountSignals(h, "servicebus:ingest", options, (_, _) =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult(new EntityCounts(5_000, 12));
            });
        });
        await using var _ = services;
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);

        var active = signals.Signals.Single(s => s.Source == "servicebus:ingest:active");
        var deadLetter = signals.Signals.Single(s => s.Source == "servicebus:ingest:dead-letter");
        Assert.Equal((5_000d, SignalSeverity.Unhealthy), (active.Value!.Value, active.Severity));
        Assert.Equal((12d, SignalSeverity.Healthy), (deadLetter.Value!.Value, deadLetter.Severity));
        Assert.Equal("servicebus", active.Tags!["origin"]);
        Assert.Equal(1, reads);
    }

    private sealed class FakeProcessor : ServiceBusProcessor
    {
        private bool _processing = true;

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public override bool IsProcessing => _processing;

        public override Task StartProcessingAsync(CancellationToken cancellationToken = default)
        {
            Starts++;
            _processing = true;
            return Task.CompletedTask;
        }

        public override Task StopProcessingAsync(CancellationToken cancellationToken = default)
        {
            Stops++;
            _processing = false;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Service_Bus_processors_stop_and_start_only_when_needed()
    {
        var processor = new FakeProcessor();
        var pausable = new PausableServiceBusProcessor("ingest", processor);

        await pausable.ResumeAsync(CancellationToken.None);                // already running
        await pausable.PauseAsync(CancellationToken.None);
        await pausable.PauseAsync(CancellationToken.None);                 // already stopped
        await pausable.ResumeAsync(CancellationToken.None);

        Assert.Equal((1, 1), (processor.Stops, processor.Starts));
    }

    // ---------------------------------------------------------------- RabbitMQ

    [Fact]
    public async Task RabbitMQ_queue_signals_flag_a_queue_without_consumers()
    {
        var (services, detector) = ProbeTests.Build(h =>
        {
            var options = new RabbitMqQueueSignalOptions { DeadLetterQueue = "thumbnails.dlq" };
            options.Messages.UnhealthyAbove = 100;
            HomeostatRabbitMqExtensions.AddQueueSignals(h, "thumbnails", options, (_, queue, _) =>
                Task.FromResult(queue == "thumbnails" ? new QueueCounts(40, 0) : new QueueCounts(7, 0)));
        });
        await using var _ = services;
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);

        var bySource = signals.Signals.ToDictionary(s => s.Source);
        Assert.Equal(SignalSeverity.Healthy, bySource["rabbitmq:thumbnails:messages"].Severity);
        Assert.Equal(SignalSeverity.Unhealthy, bySource["rabbitmq:thumbnails:consumers"].Severity);
        Assert.Equal(7, bySource["rabbitmq:thumbnails:dead-letter"].Value);
    }

    /// <summary>Implements an interface by recording the calls and answering from a function, for large client interfaces.</summary>
    public class Recorder<T> : DispatchProxy
        where T : class
    {
        public List<(string Method, object?[] Arguments)> Calls { get; } = [];

        public Func<MethodInfo, object?[], object?> Answer { get; set; } = (_, _) => null;

        public static (T Proxy, Recorder<T> Recorder) Create(Func<MethodInfo, object?[], object?> answer)
        {
            var proxy = DispatchProxy.Create<T, Recorder<T>>();
            var recorder = (Recorder<T>)(object)proxy;
            recorder.Answer = answer;
            return (proxy, recorder);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add((targetMethod!.Name, args ?? []));
            return Answer(targetMethod, args ?? []);
        }
    }

    [Fact]
    public async Task RabbitMQ_consumer_cancels_and_subscribes_again()
    {
        var tags = 0;
        var (channel, recorder) = Recorder<IChannel>.Create((method, _) => method.Name switch
        {
            "BasicConsumeAsync" => Task.FromResult($"tag-{++tags}"),
            "BasicCancelAsync" => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        });
        var consumer = new PausableRabbitMqConsumer("thumbnails", channel, "thumbnails", autoAck: false, new AsyncDefaultBasicConsumerStub(channel));

        await consumer.StartAsync();
        Assert.True(consumer.IsConsuming);
        await consumer.PauseAsync(CancellationToken.None);
        await consumer.PauseAsync(CancellationToken.None);
        Assert.False(consumer.IsConsuming);
        await consumer.ResumeAsync(CancellationToken.None);
        await consumer.ResumeAsync(CancellationToken.None);

        Assert.Equal(["BasicConsumeAsync", "BasicCancelAsync", "BasicConsumeAsync"], recorder.Calls.Select(c => c.Method));
        Assert.Equal("tag-1", recorder.Calls[1].Arguments[0]);

        // The broker cancels the subscription (queue deleted): resuming subscribes again.
        var wrapper = (IAsyncBasicConsumer)recorder.Calls[2].Arguments[6]!;
        await wrapper.HandleBasicCancelAsync("tag-2");
        Assert.False(consumer.IsConsuming);
        await consumer.ResumeAsync(CancellationToken.None);
        Assert.Equal("BasicConsumeAsync", recorder.Calls[^1].Method);
        Assert.True(consumer.IsConsuming);
    }

    [Fact]
    public async Task RabbitMQ_consumer_paused_before_starting_waits_for_the_resume()
    {
        var open = true;
        var (channel, recorder) = Recorder<IChannel>.Create((method, _) => method.Name switch
        {
            "BasicConsumeAsync" => Task.FromResult("tag-1"),
            "BasicCancelAsync" => Task.FromException(new IOException("channel closed")),
            "get_IsOpen" => open,
            _ => throw new NotSupportedException(method.Name),
        });
        var consumer = new PausableRabbitMqConsumer("thumbnails", channel, "thumbnails", autoAck: false, new AsyncDefaultBasicConsumerStub(channel));

        await consumer.PauseAsync(CancellationToken.None);                 // registered and paused before the worker starts it
        await consumer.StartAsync();
        Assert.False(consumer.IsConsuming);
        await consumer.ResumeAsync(CancellationToken.None);
        Assert.True(consumer.IsConsuming);

        // A cancel that fails on an open channel is reported (and retried); on a closed channel the subscription is gone.
        await Assert.ThrowsAsync<IOException>(() => consumer.PauseAsync(CancellationToken.None));
        Assert.True(consumer.IsConsuming);
        open = false;
        await consumer.PauseAsync(CancellationToken.None);
        Assert.False(consumer.IsConsuming);
    }

    private sealed class AsyncDefaultBasicConsumerStub(IChannel channel) : AsyncDefaultBasicConsumer(channel);

    // ---------------------------------------------------------------- Kafka

    private const string Statistics = """
        { "name": "rdkafka#consumer-1", "type": "consumer",
          "brokers": {
            "kafka-0:9092/bootstrap": { "nodeid": -1, "source": "configured", "state": "UP", "toppars": {} },
            "kafka-0:9092/0": { "nodeid": 0, "source": "learned", "state": "UP", "toppars": { "telemetry-0": {}, "telemetry-1": {} } },
            "kafka-1:9092/1": { "nodeid": 1, "source": "learned", "state": "INIT", "toppars": {} },
            "GroupCoordinator": { "nodeid": 0, "source": "logical", "state": "UP" } },
          "topics": {
            "telemetry": { "topic": "telemetry", "partitions": {
              "0": { "partition": 0, "consumer_lag": 150, "consumer_lag_stored": 120 },
              "1": { "partition": 1, "consumer_lag": 40, "consumer_lag_stored": 30 },
              "2": { "partition": 2, "consumer_lag": -1, "consumer_lag_stored": -1 },
              "-1": { "partition": -1, "consumer_lag": -1 } } } } }
        """;

    [Fact]
    public void Kafka_statistics_give_lag_brokers_and_producer_queue()
    {
        var consumer = KafkaSignalReporter.Read(Statistics, fromCommitted: false);
        Assert.Equal((150L, 2, 120L), consumer.Lag);
        Assert.Equal((1, 1), consumer.Brokers);                            // only the broker leading our partitions counts
        Assert.Null(consumer.QueueShare);
        Assert.Equal((190L, 2, 150L), KafkaSignalReporter.Read(Statistics, fromCommitted: true).Lag);
        Assert.Null(KafkaSignalReporter.Read("""{ "type": "consumer", "topics": {} }""", fromCommitted: false).Lag);

        var signals = new CollectingPublisher();
        var reporter = new KafkaSignalReporter(signals, "telemetry", new KafkaSignalOptions { LagDegradedAbove = 100, LagUnhealthyAbove = 1_000 });
        reporter.Report(Statistics);
        reporter.Report("not json");
        var lag = signals.Signals.Single(s => s.Source == "kafka:telemetry:lag");
        Assert.Equal((SignalSeverity.Degraded, 150d), (lag.Severity, lag.Value!.Value));
        Assert.Equal("120", lag.Tags!["max-partition-lag"]);
        var brokers = signals.Signals.Single(s => s.Source == "kafka:telemetry:brokers");
        Assert.Equal((SignalSeverity.Healthy, 1d), (brokers.Severity, brokers.Value!.Value));

        signals = new CollectingPublisher();
        var producer = new KafkaSignalReporter(signals, "audit");
        const string Down = """
            { "type": "producer", "msg_cnt": 10, "msg_max": 100000, "msg_size": 1060000000, "msg_size_max": 1073741824,
              "brokers": { "kafka:9092/1": { "nodeid": 1, "source": "learned", "state": "DOWN", "toppars": { "audit-0": {} } } } }
            """;
        producer.Report(Down);
        producer.Report(Down);
        producer.Report("""{ "type": 5, "brokers": [] }""");                // odd documents are skipped, never thrown
        Assert.Equal(SignalSeverity.Unhealthy, signals.Signals.First(s => s.Source == "kafka:audit:queue").Severity);   // the byte limit
        Assert.Equal(
            [SignalSeverity.Healthy, SignalSeverity.Unhealthy],                 // confirmed by the second document
            signals.Signals.Where(s => s.Source == "kafka:audit:brokers").Select(s => s.Severity));
        Assert.DoesNotContain(signals.Signals, s => s.Source == "kafka:audit:lag");
    }

    [Fact]
    public async Task Kafka_gate_pauses_the_assignment_on_the_consumer_thread()
    {
        var assignment = new List<TopicPartition> { new("telemetry", 0), new("telemetry", 1) };
        var (consumer, recorder) = Recorder<IConsumer<string, string>>.Create((method, _) => method.Name switch
        {
            "get_Assignment" => assignment.ToList(),                       // the client returns a new list each time
            "Pause" or "Resume" => null,
            _ => throw new NotSupportedException(method.Name),
        });
        var gate = new KafkaPauseGate("telemetry");

        gate.Apply(consumer);                                              // running: nothing to do
        await gate.PauseAsync(CancellationToken.None);
        gate.Apply(consumer);
        assignment.Add(new TopicPartition("telemetry", 2));                // rebalance while paused
        gate.Apply(consumer);                                              // the whole assignment again
        await gate.ResumeAsync(CancellationToken.None);
        gate.Apply(consumer);
        gate.Apply(consumer);                                              // running: nothing to do

        var calls = recorder.Calls.Where(c => c.Method is "Pause" or "Resume").ToList();
        Assert.Equal(["Pause", "Pause", "Resume"], calls.Select(c => c.Method));
        Assert.Equal(2, ((IEnumerable<TopicPartition>)calls[0].Arguments[0]!).Count());
        Assert.Equal(3, ((IEnumerable<TopicPartition>)calls[1].Arguments[0]!).Count());
        Assert.Equal(3, ((IEnumerable<TopicPartition>)calls[2].Arguments[0]!).Count());
    }

    // ---------------------------------------------------------------- Aspire

    [Fact]
    public void Aspire_commands_target_the_endpoints_under_the_prefix()
    {
        Assert.Equal("/homeostat", HomeostatResourceBuilderExtensions.NormalizePrefix("homeostat/"));
        Assert.Equal("/ops/homeostat", HomeostatResourceBuilderExtensions.NormalizePrefix("/ops/homeostat"));
        Assert.Throws<ArgumentException>(() => HomeostatResourceBuilderExtensions.NormalizePrefix("/a?b"));

        Assert.Equal("https://localhost:7001/ops/homeostat/dashboard",
            HomeostatResourceBuilderExtensions.DashboardUrl(new Uri("https://localhost:7001"), "/ops/homeostat").ToString());
        Assert.Equal("https://localhost:7001/homeostat/dashboard",                // a launchUrl in the endpoint URL
            HomeostatResourceBuilderExtensions.DashboardUrl(new Uri("https://localhost:7001/api/docs"), "/homeostat").ToString());
        Assert.Equal("for=00%3A30%3A00", HomeostatResourceBuilderExtensions.PauseQuery(TimeSpan.FromMinutes(30)));
        Assert.Null(HomeostatResourceBuilderExtensions.PauseQuery(null));
        Assert.Equal("https://localhost:7001/homeostat/pause?for=00%3A30%3A00",
            HomeostatResourceBuilderExtensions.WithQuery(new Uri("https://localhost:7001/homeostat/pause"), "for=00%3A30%3A00").ToString());
    }
}
