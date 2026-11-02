using Microsoft.Extensions.Hosting;

namespace Homeostat;

/// <summary>
/// A <see cref="BackgroundService"/> that Homeostat can restart safely with <c>RestartHostedService&lt;T&gt;()</c>.
/// Put the work loop in <see cref="RunAsync"/>. A restart cancels the current run and starts a new one inside the same
/// <c>ExecuteAsync</c>, so the generic host does not notice. Stopping and starting a plain <see cref="BackgroundService"/>
/// instead makes the host treat it as failed and, by default, stop the whole application.
/// </summary>
/// <remarks>
/// When <see cref="RunAsync"/> throws or returns, the service stays alive but idle (<see cref="IsRunning"/> is
/// <c>false</c>, <see cref="LastError"/> holds the exception) until it is restarted, instead of taking the
/// application down: <c>AddBackgroundServiceSignals()</c> turns that state into a signal a condition can heal.
/// </remarks>
/// <example>
/// <code>
/// public sealed class IngestWorker(IngestQueue queue) : RestartableBackgroundService
/// {
///     protected override async Task RunAsync(CancellationToken stoppingToken)
///     {
///         await foreach (var item in queue.ReadAllAsync(stoppingToken)) { /* ... */ }
///     }
/// }
/// </code>
/// </example>
public abstract class RestartableBackgroundService : BackgroundService
{
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private TaskCompletionSource _restartRequested = NewSignal();
    private TaskCompletionSource? _nextRunStarted;
    private static readonly AsyncLocal<RestartableBackgroundService?> CurrentRun = new();
    private bool _executing;
    private bool _idle;
    private int _restarts;

    /// <summary>Whether <see cref="RunAsync"/> is running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Whether the service is started but its run ended by itself (it threw or returned), so it waits to be restarted.
    /// <c>false</c> before the first run, while running, during a restart and when stopping.
    /// </summary>
    public bool IsIdle
    {
        get
        {
            lock (_gate)
            {
                return _idle;
            }
        }
    }

    /// <summary>Exception that ended the latest run, or <c>null</c> when it is running or ended without an error.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>How many times the service has been restarted.</summary>
    public int Restarts => Volatile.Read(ref _restarts);

    /// <summary>The work loop. Honor <paramref name="stoppingToken"/>: it is cancelled on restart and on shutdown.</summary>
    /// <param name="stoppingToken">Cancelled when the service is restarted or the application stops.</param>
    protected abstract Task RunAsync(CancellationToken stoppingToken);

    /// <summary>Called when <see cref="RunAsync"/> throws; the service then waits to be restarted.</summary>
    /// <param name="exception">The exception thrown by the run.</param>
    protected virtual void OnRunFaulted(Exception exception)
    {
    }

    /// <summary>
    /// Cancels the current run and waits until a new one has started. Restarts an idle service (after a fault) too.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting, for example at the action timeout.</param>
    /// <exception cref="InvalidOperationException">The service is not started, or is stopping.</exception>
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        if (ReferenceEquals(CurrentRun.Value, this))
        {
            throw new InvalidOperationException(
                $"{GetType().Name} cannot restart itself from RunAsync: return from RunAsync, or restart it from elsewhere.");
        }

        TaskCompletionSource started;
        CancellationTokenSource? run;
        lock (_gate)
        {
            if (!_executing)
            {
                throw new InvalidOperationException($"{GetType().Name} is not started, or is stopping: it cannot be restarted.");
            }

            started = _nextRunStarted ??= NewSignal();
            _restartRequested.TrySetResult();
            run = _run;
        }

        // Cancel outside the lock and asynchronously: cancellation callbacks run the end of the current run and the
        // start of the next one, which must not happen on this thread while holding the lock.
        if (run is not null)
        {
            try
            {
                await run.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The run ended in the meantime: the loop starts the next one anyway.
            }
        }

        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            _executing = true;
        }

        try
        {
            var first = true;
            while (!stoppingToken.IsCancellationRequested)
            {
                using var run = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                TaskCompletionSource restartRequested;
                lock (_gate)
                {
                    _run = run;
                    restartRequested = _restartRequested = NewSignal();
                    IsRunning = true;
                    _idle = false;
                    LastError = null;
                    if (!first)
                    {
                        Interlocked.Increment(ref _restarts);
                    }

                    _nextRunStarted?.TrySetResult();
                    _nextRunStarted = null;
                }

                first = false;
                try
                {
                    CurrentRun.Value = this;
                    await RunAsync(run.Token).ConfigureAwait(false);
                }
                catch (Exception) when (run.IsCancellationRequested)
                {
                    // Restart or shutdown. Cancellation often surfaces as another exception (ObjectDisposedException,
                    // ChannelClosedException...): it is not a crash.
                }
                catch (Exception ex)
                {
                    LastError = ex;
                    try
                    {
                        OnRunFaulted(ex);
                    }
                    catch (Exception)
                    {
                        // A failing hook must not end the service.
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        IsRunning = false;
                        _run = null;
                    }
                }

                if (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (!run.IsCancellationRequested)
                {
                    // The run ended by itself (returned or threw): stay idle until a restart or the shutdown.
                    lock (_gate)
                    {
                        _idle = !restartRequested.Task.IsCompleted;
                    }

                    try
                    {
                        await restartRequested.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _executing = false;
                _idle = false;
                _nextRunStarted?.TrySetCanceled();
                _nextRunStarted = null;
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
