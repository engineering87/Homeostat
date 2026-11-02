namespace Homeostat.Consumers;

/// <summary>
/// A message consumer that Homeostat can pause and resume: a Service Bus processor, a RabbitMQ consumer, a Kafka
/// consumer, or your own. Pausing stops taking new messages; messages already being handled finish.
/// </summary>
/// <remarks>
/// Consumers are paused while the switch <c>consumer:{name}:paused</c> is on (see <see cref="ConsumerSwitches"/>):
/// by a plan step (<c>PauseConsumer</c>), from the dashboard or the CLI. Several consumers can share a name, for example
/// the instances of a worker pool: they are paused and resumed together.
/// </remarks>
public interface IPausableConsumer
{
    /// <summary>Name of the consumer, as used by <c>PauseConsumer</c> and the switch.</summary>
    string Name { get; }

    /// <summary>Stops taking new messages. Called again only after <see cref="ResumeAsync"/>.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PauseAsync(CancellationToken cancellationToken);

    /// <summary>Takes messages again.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ResumeAsync(CancellationToken cancellationToken);
}

/// <summary>Registers consumers created at run time, so Homeostat can pause and resume them.</summary>
public interface IHomeostatConsumers
{
    /// <summary>
    /// Registers a consumer. If its switch is already on, the consumer is paused right away. Dispose the result when the
    /// consumer is disposed.
    /// </summary>
    /// <param name="consumer">The consumer.</param>
    IDisposable Register(IPausableConsumer consumer);

    /// <summary>Whether consumers with this name are paused (their switch is on).</summary>
    /// <param name="name">Consumer name.</param>
    bool IsPaused(string name);

    /// <summary>Names of the registered consumers.</summary>
    IReadOnlyCollection<string> Names { get; }
}

/// <summary>Names of the switches that pause consumers.</summary>
public static class ConsumerSwitches
{
    /// <summary>The switch that pauses the consumers named <paramref name="name"/>: <c>consumer:{name}:paused</c>.</summary>
    /// <param name="name">Consumer name.</param>
    public static string Paused(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return $"consumer:{name}:paused";
    }
}
