using System.Threading.Channels;
using Homeostat.Conditions;
using Homeostat.Healing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homeostat.Notifications;

/// <summary>
/// Base class for observers that deliver <see cref="HomeostatEvent"/>s somewhere slow, such as a file, a queue or an
/// HTTP endpoint. Observer callbacks only enqueue; a background loop calls <see cref="WriteAsync"/> one event at a
/// time. Register a sink with <see cref="HomeostatEventSinkExtensions.AddEventSink{TSink}"/>.
/// </summary>
/// <remarks>
/// The queue holds up to 1000 events; when the sink falls behind, the oldest are dropped. When the application
/// stops, the sink keeps accepting events until every other hosted service has stopped (so the outcome of runs
/// canceled by the shutdown is recorded), then delivers what is queued within the host's shutdown timeout.
/// </remarks>
public abstract class HomeostatEventSink : BackgroundService, IHealingObserver, IHostedLifecycleService
{
    private readonly Channel<HomeostatEvent> _queue = Channel.CreateBounded<HomeostatEvent>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private readonly CancellationTokenSource _abort = new();
    private readonly HomeostatEventFactory _events;
    private readonly HashSet<HomeostatEventKind> _kinds;
    private int _droppedAfterStop;

    /// <summary>Initializes the sink.</summary>
    /// <param name="application">Application name written in every event (usually <see cref="HomeostatOptions.ApplicationName"/>).</param>
    /// <param name="kinds">Event kinds to deliver; the others are ignored.</param>
    /// <param name="timeProvider">Clock used for event timestamps.</param>
    /// <param name="logger">Logger for delivery failures.</param>
    protected HomeostatEventSink(string application, IEnumerable<HomeostatEventKind> kinds, TimeProvider timeProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _events = new HomeostatEventFactory(application, timeProvider);
        _kinds = new HashSet<HomeostatEventKind>(kinds);
        Logger = logger;
    }

    /// <summary>Logger passed to the constructor.</summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// <c>true</c> once the application is stopping: deliver quickly (for example without retries), because the
    /// remaining shutdown time is shared with every other sink.
    /// </summary>
    protected bool IsStopping { get; private set; }

    /// <summary>Delivers one event. Exceptions are logged and the event is dropped.</summary>
    /// <param name="homeostatEvent">The event.</param>
    /// <param name="cancellationToken">Canceled when the host's shutdown timeout expires.</param>
    protected abstract Task WriteAsync(HomeostatEvent homeostatEvent, CancellationToken cancellationToken);

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
    Task IHostedLifecycleService.StoppingAsync(CancellationToken cancellationToken)
    {
        IsStopping = true;
        return Task.CompletedTask;
    }

    /// <summary>Keeps the loop running: the queue is drained in <see cref="IHostedLifecycleService.StoppedAsync"/>.</summary>
    /// <param name="cancellationToken">Not used.</param>
    public sealed override Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    async Task IHostedLifecycleService.StoppedAsync(CancellationToken cancellationToken)
    {
        // Every hosted service, the engine included, has stopped: no more events will come.
        IsStopping = true;
        _queue.Writer.TryComplete();

        using var registration = cancellationToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), _abort);
        if (ExecuteTask is { } loop)
        {
            await Task.WhenAny(loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }

        var dropped = Volatile.Read(ref _droppedAfterStop);
        if (dropped > 0)
        {
            Logger.LogWarning("{Sink} dropped {Count} event(s) raised after the application stopped.", GetType().Name, dropped);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The stopping token is ignored on purpose: the loop ends when the queue is completed and drained,
        // or when the shutdown timeout expires.
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_abort.Token).ConfigureAwait(false))
            {
                try
                {
                    await WriteAsync(item, _abort.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_abort.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "{Sink} could not deliver a {Kind} event for condition {Condition}.", GetType().Name, item.Kind, item.Condition);
                }
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            Logger.LogWarning("{Sink} stopped before delivering all queued events.", GetType().Name);
        }
    }

    private void Enqueue(HomeostatEventKind kind, Func<HomeostatEvent> create)
    {
        if (_kinds.Contains(kind) && !_queue.Writer.TryWrite(create()))
        {
            Interlocked.Increment(ref _droppedAfterStop);
        }
    }
}
