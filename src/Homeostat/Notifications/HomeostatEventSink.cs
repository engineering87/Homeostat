using System.Threading.Channels;
using Homeostat.Conditions;
using Homeostat.Healing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homeostat.Notifications;

/// <summary>
/// Delivers <see cref="HomeostatEvent"/>s somewhere slow, such as a file, a queue or an HTTP endpoint. Register it with
/// <see cref="HomeostatEventSinkExtensions.AddEventSink{TSink}"/>: Homeostat queues the events and calls
/// <see cref="WriteAsync"/> one at a time, in the background.
/// </summary>
/// <remarks>
/// The queue holds up to 1000 events; when the sink falls behind, the oldest are dropped. When the application stops,
/// the sink keeps receiving events until every other hosted service has stopped (so the outcome of runs canceled by the
/// shutdown is recorded), then what is queued is delivered within the host's shutdown timeout: a sink that retries
/// should stop retrying once <c>IHostApplicationLifetime.ApplicationStopping</c> is signaled.
/// </remarks>
/// <example>
/// <code>
/// public sealed class IncidentLogSink(IncidentLog log) : IHomeostatEventSink
/// {
///     public IReadOnlyCollection&lt;HomeostatEventKind&gt;? Events => [HomeostatEventKind.RunCompleted];
///
///     public Task WriteAsync(HomeostatEvent homeostatEvent, CancellationToken cancellationToken)
///         => log.AppendAsync(homeostatEvent.Summary, cancellationToken);
/// }
///
/// builder.Services.AddHomeostat(h => h.AddEventSink&lt;IncidentLogSink&gt;());
/// </code>
/// </example>
public interface IHomeostatEventSink
{
    /// <summary>The kinds of event to deliver; <c>null</c> (the default) delivers every kind.</summary>
    IReadOnlyCollection<HomeostatEventKind>? Events => null;

    /// <summary>Delivers one event. Exceptions are logged and the event is dropped.</summary>
    /// <param name="homeostatEvent">The event.</param>
    /// <param name="cancellationToken">Canceled when the host's shutdown timeout expires.</param>
    Task WriteAsync(HomeostatEvent homeostatEvent, CancellationToken cancellationToken);
}

/// <summary>The queues of every registered sink, completed together when the application has stopped.</summary>
internal sealed class EventSinkQueues
{
    private readonly List<ChannelWriter<HomeostatEvent>> _writers = [];

    public void Add(ChannelWriter<HomeostatEvent> writer)
    {
        lock (_writers)
        {
            _writers.Add(writer);
        }
    }

    public void CompleteAll()
    {
        lock (_writers)
        {
            foreach (var writer in _writers)
            {
                writer.TryComplete();
            }
        }
    }
}

/// <summary>Queues the events for one <see cref="IHomeostatEventSink"/> and delivers them in the background.</summary>
internal sealed class EventSinkHost<TSink> : BackgroundService, IHealingObserver, IHostedLifecycleService
    where TSink : class, IHomeostatEventSink
{
    internal const int QueueSize = 1000;

    private readonly Channel<HomeostatEvent> _queue;

    private readonly CancellationTokenSource _abort = new();
    private readonly HomeostatEventFactory _events;
    private readonly HashSet<HomeostatEventKind> _kinds;
    private readonly TSink _sink;
    private readonly ILogger _logger;
    private readonly string _name = typeof(TSink).Name;
    private int _droppedAfterStop;

    // Events dropped from the full queue (the sink is slower than the events), reported at most once a minute.
    private readonly TimeProvider _timeProvider;
    private readonly object _dropGate = new();
    private long _droppedFull;
    private DateTimeOffset _lastDropWarning = DateTimeOffset.MinValue;

    private readonly EventSinkQueues _queues;

    public EventSinkHost(
        TSink sink,
        EventSinkQueues queues,
        Microsoft.Extensions.Options.IOptions<HomeostatOptions> options,
        TimeProvider timeProvider,
        ILogger<EventSinkHost<TSink>> logger)
    {
        _sink = sink;
        _timeProvider = timeProvider;
        _queue = Channel.CreateBounded<HomeostatEvent>(
            new BoundedChannelOptions(QueueSize) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }, OnDropped);
        _queues = queues;
        queues.Add(_queue.Writer);
        _events = new HomeostatEventFactory(options.Value.ApplicationName, timeProvider);
        _kinds = new HashSet<HomeostatEventKind>(sink.Events ?? Enum.GetValues<HomeostatEventKind>());
        _logger = logger;
    }

    /// <inheritdoc />
    public void OnConditionActivated(ConditionSnapshot condition) => Enqueue(HomeostatEventKind.ConditionActivated, () => _events.Activated(condition));

    /// <inheritdoc />
    public void OnConditionCleared(ConditionSnapshot condition) => Enqueue(HomeostatEventKind.ConditionCleared, () => _events.Cleared(condition));

    /// <inheritdoc />
    public void OnStepCompleted(HealingStepResult step) => Enqueue(HomeostatEventKind.StepCompleted, () => _events.Step(step));

    /// <inheritdoc />
    public void OnRunCompleted(HealingRunResult run) => Enqueue(HomeostatEventKind.RunCompleted, () => _events.Run(run));

    /// <inheritdoc />
    public void OnApprovalRequested(PendingApproval approval) => Enqueue(HomeostatEventKind.ApprovalRequested, () => _events.ApprovalRequested(approval));

    /// <inheritdoc />
    public void OnApprovalResolved(ApprovalResolution resolution) => Enqueue(HomeostatEventKind.ApprovalResolved, () => _events.ApprovalResolved(resolution));

    /// <inheritdoc />
    Task IHostedLifecycleService.StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    Task IHostedLifecycleService.StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    Task IHostedLifecycleService.StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Keeps the loop running: the queue is drained in <see cref="IHostedLifecycleService.StoppedAsync"/>.</summary>
    public override Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    async Task IHostedLifecycleService.StoppedAsync(CancellationToken cancellationToken)
    {
        // Every hosted service, the engine included, has stopped: no more events will come. Every sink's queue is
        // completed now, so all of them drain at the same time: one slow sink does not use up the shutdown timeout
        // of the sinks the host stops after it.
        _queues.CompleteAll();

        using var registration = cancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), _abort);
        if (ExecuteTask is { } loop)
        {
            await Task.WhenAny(loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }

        ReportDropped(force: true);
        var dropped = Volatile.Read(ref _droppedAfterStop);
        if (dropped > 0)
        {
            _logger.LogWarning("{Sink} dropped {Count} event(s) raised after the application stopped.", _name, dropped);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The stopping token is ignored on purpose: the loop ends when the queue is completed and drained,
        // or when the shutdown timeout expires.
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_abort.Token).ConfigureAwait(false))
            {
                try
                {
                    await _sink.WriteAsync(item, _abort.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_abort.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Sink} could not deliver a {Kind} event for condition {Condition}.", _name, item.Kind, item.Condition);
                }
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            _logger.LogWarning("{Sink} stopped before delivering all queued events.", _name);
        }
    }

    private void OnDropped(HomeostatEvent dropped)
    {
        lock (_dropGate)
        {
            _droppedFull++;
        }

        ReportDropped(force: false);
    }

    private void ReportDropped(bool force)
    {
        long count;
        var now = _timeProvider.GetUtcNow();
        lock (_dropGate)
        {
            if (_droppedFull == 0 || (!force && now - _lastDropWarning < TimeSpan.FromMinutes(1)))
            {
                return;
            }

            count = _droppedFull;
            _droppedFull = 0;
            _lastDropWarning = now;
        }

        _logger.LogWarning(
            "{Sink} dropped {Count} event(s), the oldest, because its queue of {Size} was full: it delivers more slowly than events are raised.",
            _name, count, QueueSize);
    }

    private void Enqueue(HomeostatEventKind kind, Func<HomeostatEvent> create)
    {
        if (_kinds.Contains(kind) && !_queue.Writer.TryWrite(create()))
        {
            Interlocked.Increment(ref _droppedAfterStop);
        }
    }
}
