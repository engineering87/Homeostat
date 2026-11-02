# Message brokers

Consumers are where many .NET services break quietly: the backlog grows, messages pile up in the dead-letter queue,
a consumer is cancelled by the broker and nobody notices. Four packages bring the main brokers into view, and every
broker gets the same healing tool: **pause the consumer** while what it writes to is down, instead of failing every
message into the dead-letter queue.

| Broker | Package | Signals | Pausable consumer |
|---|---|---|---|
| Azure Service Bus | `Homeostat.ServiceBus` | `servicebus:{queue}:active`, `:dead-letter` (also for subscriptions) | `ServiceBusProcessor`, `ServiceBusSessionProcessor` |
| RabbitMQ | `Homeostat.RabbitMQ` | `rabbitmq:{queue}:messages`, `:consumers`, `:dead-letter` | `PausableRabbitMqConsumer` |
| Apache Kafka | `Homeostat.Kafka` | `kafka:{name}:lag`, `:brokers`, `:queue` (producers) | `KafkaPauseGate` |
| Apache ActiveMQ Artemis | `Homeostat.Artemis` | `artemis:{queue}:messages`, `:consumers`, `:dead-letter` | `PauseGate` in the receive loop |
| Any other | `Homeostat` | your own [probes](integrations.md#probes-any-number-as-a-signal) | your own `IPausableConsumer` |

Signal values are counts, so every condition rule applies: a threshold for the backlog, a **trend** for dead letters
(they pile up until someone handles them, so what matters is how fast they grow), an anomaly for lag.

## Pausing consumers

A consumer that Homeostat can pause implements `IPausableConsumer` (the packages below provide adapters). It is
paused while the switch `consumer:{name}:paused` is on: turned on by the plan step `PauseConsumer(name)`, or by hand
from the dashboard or the CLI (`homeostat switch on consumer:ingest:paused`).

```csharp
h.AddCondition("archive-db-down", c => c
    .ForSource("archive-db")                       // a database probe
    .SustainedFor(TimeSpan.FromSeconds(30))
    .Heal(p => p
        .PauseConsumer("ingest")                   // stop taking messages; resumes when the condition clears
        .Then<FailoverArchiveAction>().RequireApproval()));
```

- `PauseConsumer(name, resumeWhenCleared: true)` resumes the consumer when the condition clears. With `false`, it
  stays paused until a person turns the switch off.
- Several consumers can share a name (the instances of a worker pool): they pause and resume together. Each
  application instance pauses its own consumers.
- Pausing stops taking **new** messages. What happens to the messages in progress depends on the broker: see each
  section below.
- Consumers declared with `AddPausableConsumer` are registered when the host starts. Consumers created later register
  themselves with `IHomeostatConsumers.Register(consumer)`; if their switch is already on, they are paused at once.
- A failed pause or resume is logged and **retried** (after 5, 15, 30 seconds, then every minute) until the consumer
  matches its switch. Each call has a 2-minute limit, so a consumer stuck waiting for its handlers does not block the
  next change.
- The step is a switch, so it follows every guardrail: dry-run, budget, approvals. A switch that a person turned on
  is never turned off by a condition clearing: the person turns it off.
- **Order of start-up**: a consumer that your code starts after registering it must not start consuming if it is
  paused. `PausableRabbitMqConsumer.StartAsync` and `KafkaPauseGate` handle this; for Service Bus, do not call
  `StartProcessingAsync` again on a processor that Homeostat may have paused (for example from a worker that restarts
  it): let the processor's owner start it once, and Homeostat stop and start it afterwards.

## Azure Service Bus

```csharp
builder.Services.AddSingleton(new ServiceBusAdministrationClient("ingest-ns.servicebus.windows.net", new DefaultAzureCredential()));
builder.Services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateProcessor("ingest"));

builder.Services.AddHomeostat(h => h
    .AddServiceBusQueueSignals("ingest", o =>
    {
        o.Active.UnhealthyAbove = 10_000;          // backlog
        o.Interval = TimeSpan.FromSeconds(30);
    })
    .AddServiceBusSubscriptionSignals("events", "billing")
    .AddPausableServiceBusProcessor("ingest")      // the ServiceBusProcessor in the services
    .AddCondition("ingest-dead-letters", c => c
        .ForSource("servicebus:ingest:dead-letter")
        .WhenRisingFasterThan(perMinute: 5, window: TimeSpan.FromMinutes(15))
        .Heal(p => p.PauseConsumer("ingest", resumeWhenCleared: false))));
```

- The counts come from `ServiceBusAdministrationClient`, which needs the **Manage** right or the *Azure Service Bus
  Data Owner* role. One read per interval feeds both signals. Service Bus refreshes the counts about every 30 seconds.
- Dead letters include transfer dead letters.
- Pausing calls `StopProcessingAsync`, which waits for the handlers in progress **and cancels their
  `CancellationToken`**: a handler that honours it gives up, and its message is abandoned and delivered again later,
  with its delivery count increased (after `MaxDeliveryCount` deliveries it is dead-lettered). Handlers that should
  finish their message must not pass that token to the work. Resuming calls `StartProcessingAsync`. Neither is called
  when the processor is already in that state. For session processors use `AddPausableServiceBusSessionProcessor`.
- Without a factory, `AddPausableServiceBusProcessor` wraps the `ServiceBusProcessor` registered in the services.
  With several processors, pass the factory for each: `AddPausableServiceBusProcessor("billing", sp => ...)`.

## RabbitMQ

```csharp
builder.Services.AddSingleton<IConnection>(_ => new ConnectionFactory { HostName = "rabbitmq" }.CreateConnectionAsync().GetAwaiter().GetResult());

builder.Services.AddHomeostat(h => h
    .AddRabbitMqQueueSignals("thumbnails", o =>
    {
        o.Messages.UnhealthyAbove = 50_000;
        o.DeadLetterQueue = "thumbnails.dead-letter";
    }));
```

- Each read is a passive queue declare on a **short-lived channel** of the application's `IConnection`: a missing
  queue closes that channel only, and the probe reports the failure (Unhealthy, without a value).
- `rabbitmq:{queue}:consumers` is Unhealthy when the queue has **no consumer** (`Consumers.UnhealthyBelow = 1`): the
  broker cancelled them, or the consumer crashed. If the application pauses its own consumer, set
  `o.Consumers.UnhealthyBelow = null`, or suppress the condition while paused.

Consume through `PausableRabbitMqConsumer` instead of calling `BasicConsumeAsync` yourself:

```csharp
public sealed class ThumbnailWorker(IConnection connection, IHomeostatConsumers consumers) : RestartableBackgroundService
{
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.BasicQosAsync(0, 20, false, stoppingToken);
        var consumer = new PausableRabbitMqConsumer("thumbnails", channel, "thumbnails", autoAck: false, new ThumbnailConsumer(channel));
        await consumer.StartAsync(stoppingToken);
        using var registration = consumers.Register(consumer);
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
```

Pausing cancels the subscription (`BasicCancelAsync`): the broker stops delivering, and messages already delivered
are still handled and acknowledged. Resuming subscribes again. If the broker cancels the subscription itself (queue
deleted, node failure), the next resume subscribes again; a pause that fails because the channel is closed counts
as done, since the subscription went with the channel.

## Apache Kafka

The Kafka client already knows the lag of every assigned partition and the state of its broker connections, and
reports them in its statistics, so the signals need no extra connection or query:

```csharp
var consumer = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
    {
        BootstrapServers = "kafka:9092",
        GroupId = "telemetry",
        StatisticsIntervalMs = 30_000,             // required: no statistics, no signal
    })
    .SetHomeostatSignals(services, "telemetry", o => o.LagUnhealthyAbove = 100_000)
    .Build();

var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = "kafka:9092", StatisticsIntervalMs = 30_000 })
    .SetHomeostatSignals(services, "audit")
    .Build();
```

| Signal | Client | Value | Severity |
|---|---|---|---|
| `kafka:{name}:brokers` | both | brokers connected (`UP`) among those leading the client's partitions (the client connects to the others only when needed); before any partition, any broker up | Unhealthy when none is up, Degraded when some are down, after two statistics documents in a row |
| `kafka:{name}:lag` | consumer | messages behind, summed over the partitions | `LagDegradedAbove`, `LagUnhealthyAbove` |
| `kafka:{name}:queue` | producer | share of the local queue in use: the fuller of messages (`msg_cnt / msg_max`) and bytes (`msg_size / msg_size_max`) | Degraded above 0.8, Unhealthy above 0.95 |

A producer whose brokers are down keeps accepting messages into its local queue until it is full, then `Produce`
fails with "Local: Queue full": the queue signal shows it coming, in time to turn on a degraded mode.

- `kafka:{name}:lag` is the total lag over the partitions in the statistics with a known lag, from the **stored**
  offsets (set `FromCommittedOffsets` to use the committed ones). By default the client stores an offset when `Consume`
  returns the message, before your code handles it: for a lag of what was handled, set `EnableAutoOffsetStore = false`
  and call `StoreOffset` after handling. The tags carry the number of partitions and the largest partition lag;
  before the first assignment nothing is published.
- A client has one statistics handler: to keep yours, create a `KafkaSignalReporter` and call `Report(json)` from it.
  Unexpected documents are skipped: the handler never throws, since the client would rethrow from the next `Consume`.
- While the consumer is paused it fetches nothing, so the lag it reports can stop moving: judge the lag while running.

The Kafka consumer is not thread-safe, so Homeostat cannot pause it from another thread. A `KafkaPauseGate` records
the request, and your consume loop applies it:

```csharp
var gate = new KafkaPauseGate("telemetry");
using var registration = consumers.Register(gate);
consumer.Subscribe("telemetry");
while (!stoppingToken.IsCancellationRequested)
{
    gate.Apply(consumer);                          // pauses or resumes the assigned partitions, on this thread
    var result = consumer.Consume(TimeSpan.FromSeconds(1));
    if (result is not null)
    {
        await HandleAsync(result, stoppingToken);
    }
}
```

While paused, `Consume` returns no messages but the consumer stays in its group (no rebalance). Partitions assigned
later are paused at the next `Apply`, so a `Consume` right after a rebalance can still return a message from them.
Call `Consume` with a timeout, so the loop keeps running and applies the resume.

## Apache ActiveMQ Artemis

`Homeostat.Artemis` reads the queues through the broker's **management API** (Jolokia, on the web console port), so
it needs no AMQP or JMS client and works whatever client the application consumes with:

```csharp
builder.Services.AddHomeostat(h => h
    .AddArtemis(o =>
    {
        o.ManagementUrl = new Uri("http://artemis:8161");
        o.BrokerName = "0.0.0.0";                     // <name> in broker.xml
        o.UserName = "monitor";
        o.Password = builder.Configuration["Artemis:MonitorPassword"];
    })
    .AddArtemisQueueSignals("invoices", q =>
    {
        q.Messages.UnhealthyAbove = 20_000;
        q.DeadLetterQueue = "DLQ.invoices";           // with auto-create-dead-letter-resources; "DLQ" when shared
    })
    .AddArtemisQueueSignals("events.audit", q => { q.Address = "events"; q.RoutingType = "multicast"; }));
```

- One request per interval reads `MessageCount` and `ConsumerCount` of the queue's MBean (a second one reads the
  dead-letter queue)
  (`org.apache.activemq.artemis:broker=...,component=addresses,address=...,subcomponent=queues,routing-type=...,queue=...`).
  A missing queue makes the probe fail (Unhealthy, without a value).
- `artemis:{queue}:consumers` is Unhealthy when the queue has no consumer, as for RabbitMQ.
- Dead letters: the default configuration sends every address to one shared queue, `DLQ`, so its count is
  broker-wide. With `auto-create-dead-letter-resources`, each address gets its own queue, `DLQ.{address}` on the address
  `DLQ`: set `DeadLetterQueue` to it (and `DeadLetterAddress` if you changed the dead-letter address).
- `ManagementUrl` can carry a path (a console behind a reverse proxy): `JolokiaPath` is appended to it. Credentials go in
  `UserName` and `Password`, never in the URL; use https when the broker is not on a private network.
- **Access**: the user needs a role allowed to read in `management.xml`, and the `Origin` header that Homeostat sends
  (by default the origin of `ManagementUrl`) must be allowed in `jolokia-access.xml`, which by default accepts only
  `localhost`. Set `o.Origin` to an allowed value, or add the origin there. Use a read-only monitoring user.

Clients that receive in a loop (for example ArtemisNetClient over AMQP, below) are paused with a `PauseGate`:

```csharp
var gate = new PauseGate("invoices");
using var registration = consumers.Register(gate);
while (!stoppingToken.IsCancellationRequested)
{
    await gate.WaitWhilePausedAsync(stoppingToken);     // returns at once unless paused
    var message = await consumer.ReceiveAsync(stoppingToken);
    await HandleAsync(message, stoppingToken);
    await consumer.AcceptAsync(message);
}
```

While paused, the loop stops receiving; messages the client has already prefetched (its link credit) stay with it
until the loop resumes, so keep the credit small for consumers you pause, or close and reopen the consumer from
`PauseAsync` and `ResumeAsync` of your own `IPausableConsumer`.

## Your own consumers

```csharp
public sealed class ImportQueueConsumer : IPausableConsumer
{
    public string Name => "imports";
    public Task PauseAsync(CancellationToken cancellationToken) { /* stop polling */ return Task.CompletedTask; }
    public Task ResumeAsync(CancellationToken cancellationToken) { /* poll again */ return Task.CompletedTask; }
}

h.AddPausableConsumer(sp => sp.GetRequiredService<ImportQueueConsumer>());
```

For a loop that pulls work (a polling worker, a change stream), a `PauseGate` is enough: the loop awaits
`WaitWhilePausedAsync` before taking the next item.

Watch the backlog of any other broker with a [probe](integrations.md#probes-any-number-as-a-signal).
