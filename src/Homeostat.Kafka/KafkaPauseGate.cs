using Confluent.Kafka;
using Homeostat.Consumers;

namespace Homeostat.Kafka;

/// <summary>
/// Lets Homeostat pause and resume a Kafka consumer safely. The Kafka consumer is not thread-safe, so Homeostat only
/// records the request; your consume loop calls <see cref="Apply{TKey, TValue}"/> before each <c>Consume</c>, on the
/// consumer's own thread, which pauses or resumes the assigned partitions. While paused, <c>Consume</c> returns no
/// messages but keeps the consumer in its group (no rebalance). Partitions assigned later are paused at the next
/// <c>Apply</c>, so a <c>Consume</c> right after a rebalance can still return a message from them.
/// </summary>
/// <example>
/// <code>
/// var gate = new KafkaPauseGate("telemetry");
/// using var registration = sp.GetRequiredService&lt;IHomeostatConsumers&gt;().Register(gate);
/// while (!stoppingToken.IsCancellationRequested)
/// {
///     gate.Apply(consumer);
///     var result = consumer.Consume(TimeSpan.FromSeconds(1));
///     if (result is not null) await HandleAsync(result, stoppingToken);
/// }
/// </code>
/// </example>
public sealed class KafkaPauseGate : IPausableConsumer
{
    private readonly HashSet<TopicPartition> _paused = [];
    private volatile bool _pauseRequested;

    /// <summary>Creates a gate with no pause requested. To share it through the services, use <c>AddKafkaPauseGate</c>.</summary>
    /// <param name="name">Consumer name, used by <c>PauseConsumer(name)</c>.</param>
    public KafkaPauseGate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Whether a pause is requested (it takes effect at the next <see cref="Apply{TKey, TValue}"/>).</summary>
    public bool IsPauseRequested => _pauseRequested;

    /// <summary>Requests a pause: the next <see cref="Apply{TKey, TValue}"/> pauses the assigned partitions. Returns at once.</summary>
    /// <param name="cancellationToken">Not used.</param>
    public Task PauseAsync(CancellationToken cancellationToken)
    {
        _pauseRequested = true;
        return Task.CompletedTask;
    }

    /// <summary>Requests a resume: the next <see cref="Apply{TKey, TValue}"/> resumes the partitions it paused. Returns at once.</summary>
    /// <param name="cancellationToken">Not used.</param>
    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        _pauseRequested = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pauses or resumes the assigned partitions as requested. Call it from the thread that calls <c>Consume</c>,
    /// before each call. While a pause is requested it pauses the whole assignment each time (an idempotent call).
    /// </summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <typeparam name="TValue">Value type.</typeparam>
    /// <param name="consumer">The consumer.</param>
    /// <param name="pausedByApplication">
    /// Partitions the application paused itself (for backpressure): a resume by Homeostat leaves them paused. Optional.
    /// </param>
    public void Apply<TKey, TValue>(IConsumer<TKey, TValue> consumer, IReadOnlyCollection<TopicPartition>? pausedByApplication = null)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var assignment = consumer.Assignment;

        // Revoked partitions are no longer ours to resume.
        _paused.IntersectWith(assignment);

        if (_pauseRequested)
        {
            // The whole assignment, every time: pausing is idempotent, and a rebalance that revokes and assigns the
            // same partitions between two calls (eager protocol) may have dropped the pause.
            if (assignment.Count > 0)
            {
                consumer.Pause(assignment);
                _paused.UnionWith(assignment);
            }
        }
        else if (_paused.Count > 0)
        {
            if (pausedByApplication is { Count: > 0 })
            {
                _paused.ExceptWith(pausedByApplication);
            }

            if (_paused.Count > 0)
            {
                consumer.Resume(_paused.ToList());
            }

            _paused.Clear();
        }
    }
}
