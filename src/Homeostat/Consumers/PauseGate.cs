namespace Homeostat.Consumers;

/// <summary>
/// A pause point for consumers that pull messages in a loop (an AMQP receiver, a change stream, a polling worker):
/// the loop awaits <see cref="WaitWhilePausedAsync"/> before taking the next message, and Homeostat pauses and resumes
/// it like any <see cref="IPausableConsumer"/>.
/// </summary>
/// <example>
/// <code>
/// var gate = new PauseGate("invoices");
/// using var registration = consumers.Register(gate);
/// while (!stoppingToken.IsCancellationRequested)
/// {
///     await gate.WaitWhilePausedAsync(stoppingToken);
///     var message = await receiver.ReceiveAsync(stoppingToken);
///     await HandleAsync(message, stoppingToken);
/// }
/// </code>
/// </example>
public sealed class PauseGate : IPausableConsumer
{
    private readonly object _gate = new();
    private TaskCompletionSource? _resumed;

    /// <summary>Creates a gate, open.</summary>
    /// <param name="name">Consumer name, used by <c>PauseConsumer(name)</c>.</param>
    public PauseGate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Whether the gate is closed.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _resumed is not null;
            }
        }
    }

    /// <summary>Completes at once when the gate is open, otherwise when it opens (or the token is cancelled).</summary>
    /// <param name="cancellationToken">Cancellation token, for example the worker's stopping token.</param>
    public Task WaitWhilePausedAsync(CancellationToken cancellationToken = default)
    {
        Task resumed;
        lock (_gate)
        {
            if (_resumed is null)
            {
                return Task.CompletedTask;
            }

            resumed = _resumed.Task;
        }

        return resumed.WaitAsync(cancellationToken);
    }

    /// <summary>Closes the gate: the loop stops at its next <see cref="WaitWhilePausedAsync"/>.</summary>
    /// <param name="cancellationToken">Not used.</param>
    public Task PauseAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        return Task.CompletedTask;
    }

    /// <summary>Opens the gate.</summary>
    /// <param name="cancellationToken">Not used.</param>
    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? resumed;
        lock (_gate)
        {
            resumed = _resumed;
            _resumed = null;
        }

        resumed?.TrySetResult();
        return Task.CompletedTask;
    }
}
