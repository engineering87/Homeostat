using System.Threading.Channels;
using Homeostat.Persistence;
using Homeostat.Signals;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Diagnostics;

/// <summary>
/// Keeps the condition history: the latest events in process memory, and in the store when it supports it, written in
/// the background so that a slow or failing store never delays the engine.
/// </summary>
internal sealed class ConditionHistory : IHostedService
{
    private const int MaxMessageLength = 300;

    private readonly IHomeostatStore _store;
    private readonly HomeostatOptions _options;
    private readonly ILogger<ConditionHistory> _logger;
    private readonly Channel<ConditionEventRecord> _queue;
    private readonly LinkedList<ConditionEventRecord> _recent = new();
    private readonly object _gate = new();
    private Task _loop = Task.CompletedTask;
    private bool _failing;

    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _timeProvider;

    public ConditionHistory(IHomeostatStore store, IOptions<HomeostatOptions> options, ILogger<ConditionHistory> logger, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _store = store;
        _options = options.Value;
        _logger = logger;
        _queue = Channel.CreateBounded<ConditionEventRecord>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    /// <summary>Whether the history is kept by the store (and so survives restarts and spans instances).</summary>
    public bool Persisted => _store.SupportsConditionHistory && _options.ConditionHistorySize > 0;

    public void Record(string condition, ConditionEventKind kind, Signal signal, DateTimeOffset at)
    {
        var size = _options.ConditionHistorySize;
        if (size <= 0)
        {
            return;
        }

        var record = new ConditionEventRecord
        {
            ConditionName = condition,
            Kind = kind,
            At = at,
            Instance = _options.InstanceId,
            Source = signal.Source,
            Severity = signal.Severity,
            Value = signal.Value is { } value && double.IsFinite(value) ? value : null,
            Message = signal.Message is { Length: > MaxMessageLength } text ? text[..MaxMessageLength] : signal.Message,
        };

        lock (_gate)
        {
            _recent.AddFirst(record);
            while (_recent.Count > size)
            {
                _recent.RemoveLast();
            }
        }

        if (_store.SupportsConditionHistory)
        {
            _queue.Writer.TryWrite(record);
        }
    }

    /// <summary>The latest events of this process, newest first.</summary>
    public IReadOnlyList<ConditionEventRecord> Recent()
    {
        lock (_gate)
        {
            return [.. _recent];
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(WriteLoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stops after the engine (hosted services stop in reverse order): writes what is queued, within the timeout.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        try
        {
            await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown timeout: the remaining events are lost.
        }
    }

    /// <summary>
    /// The history to show: the store's events merged with this process's recent ones, which covers the events not
    /// written yet (or not written because the store was down); only this process's when the store cannot be read.
    /// </summary>
    public async ValueTask<IReadOnlyList<ConditionEventRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        var recent = Recent();
        if (!Persisted)
        {
            return recent;
        }

        IReadOnlyList<ConditionEventRecord> stored;
        try
        {
            stored = await Engine.SharedControl.Bounded(
                () => _store.GetConditionEventsAsync(_options.ConditionHistorySize, cancellationToken),
                Engine.SharedControl.ReadTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Reading the condition history from the store failed; showing this instance's events only.");
            return recent;
        }

        // This instance's events the store does not have yet (one lookup, not a scan per event).
        var known = stored.Select(Key).ToHashSet();
        var missing = recent.Where(r =>
        {
            var (condition, kind, instance, ms) = Key(r);
            return !known.Contains((condition, kind, instance, ms)) && !known.Contains((condition, kind, instance, ms - 1))
                && !known.Contains((condition, kind, instance, ms + 1));
        });
        return [.. stored.Concat(missing).OrderByDescending(e => e.At).Take(_options.ConditionHistorySize)];
    }

    // The identity of an event, at the millisecond (a store may round timestamps to it).
    private static (string Condition, ConditionEventKind Kind, string? Instance, long Milliseconds) Key(ConditionEventRecord e)
        => (e.ConditionName, e.Kind, e.Instance, e.At.UtcTicks / TimeSpan.TicksPerMillisecond);

    private async Task WriteLoopAsync()
    {
        // Events the store refused are kept (up to 1000, the oldest dropped) and retried, so an outage leaves no gap.
        var pending = new Queue<ConditionEventRecord>();
        var reader = _queue.Reader;
        var completed = false;
        while (!completed || pending.Count > 0)
        {
            if (pending.Count == 0)
            {
                completed = !await reader.WaitToReadAsync().ConfigureAwait(false);
            }
            else if (!completed)
            {
                var wait = reader.WaitToReadAsync().AsTask();
                if (await Task.WhenAny(wait, Task.Delay(RetryInterval, _timeProvider)).ConfigureAwait(false) == wait)
                {
                    completed = !await wait.ConfigureAwait(false);
                }
            }

            while (reader.TryRead(out var record))
            {
                pending.Enqueue(record);
                if (pending.Count > 1000)
                {
                    pending.Dequeue();
                }
            }

            while (pending.Count > 0)
            {
                try
                {
                    var next = pending.Peek();
                    await Engine.SharedControl.Bounded(() => _store.AppendConditionEventAsync(next, _options.ConditionHistorySize), Engine.SharedControl.WriteTimeout, CancellationToken.None).ConfigureAwait(false);
                    pending.Dequeue();
                    if (_failing)
                    {
                        _failing = false;
                        _logger.LogInformation("The condition history is written to the store again.");
                    }
                }
                catch (Exception ex)
                {
                    if (!_failing)
                    {
                        _failing = true;
                        _logger.LogWarning(ex, "Writing the condition history to the store failed; events are retried every {Interval}.", RetryInterval);
                    }

                    break;
                }
            }

            if (completed && pending.Count > 0)
            {
                return; // Stopping with the store still failing: what is left is lost.
            }
        }
    }
}
