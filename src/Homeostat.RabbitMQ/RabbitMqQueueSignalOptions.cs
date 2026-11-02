using Homeostat.Probes;
using RabbitMQ.Client;

namespace Homeostat.RabbitMQ;

/// <summary>Settings of the signals of a RabbitMQ queue.</summary>
public sealed class RabbitMqQueueSignalOptions
{
    /// <summary>
    /// Source prefix of the signals: <c>{prefix}:messages</c> (ready messages) and <c>{prefix}:consumers</c>.
    /// Defaults to <c>rabbitmq:{queue}</c>.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>How often the queue is read; defaults to 30 seconds.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Thresholds of the ready message count (the backlog), for example <c>UnhealthyAbove = 50_000</c>.</summary>
    public ProbeOptions Messages { get; } = new();

    /// <summary>
    /// Thresholds of the consumer count. By default a queue without consumers is Unhealthy (<c>UnhealthyBelow = 1</c>):
    /// when this application is the consumer, its consumers died or were cancelled by the broker. Set it to <c>null</c>
    /// when consumers are expected to be absent, for example while they are paused.
    /// </summary>
    public ProbeOptions Consumers { get; } = new() { UnhealthyBelow = 1 };

    /// <summary>
    /// A dead-letter queue to watch as well, published as <c>{prefix}:dead-letter</c> with its message count. Dead
    /// letters accumulate until someone handles them: watch the count with <c>WhenRisingFasterThan</c>.
    /// </summary>
    public string? DeadLetterQueue { get; set; }

    /// <summary>Thresholds of the dead-letter count.</summary>
    public ProbeOptions DeadLetter { get; } = new();

    /// <summary>Returns the connection; by default the <see cref="IConnection"/> registered in the services.</summary>
    public Func<IServiceProvider, IConnection>? Connection { get; set; }
}
