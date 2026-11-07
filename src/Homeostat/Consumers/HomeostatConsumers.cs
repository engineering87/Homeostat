using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Homeostat.Consumers;

/// <summary>Keeps registered consumers in line with their switches.</summary>
internal sealed class HomeostatConsumers : IHomeostatConsumers, IDisposable
{
    private readonly ConcurrentDictionary<IPausableConsumer, Entry> _consumers = new(ReferenceEqualityComparer.Instance);
    private readonly IHomeostatSwitches _switches;
    private readonly ILogger<HomeostatConsumers> _logger;

    /// <summary>Longest wait for a pause or a resume: a processor waiting for a stuck handler must not block the next change.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Retry delays after a failed pause or resume; the last one repeats until it succeeds or the switch flips back.</summary>
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)];

    private readonly TimeProvider _timeProvider;

    public HomeostatConsumers(IHomeostatSwitches switches, ILogger<HomeostatConsumers> logger, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _switches = switches;
        _logger = logger;
        _switches.Changed += OnSwitchChanged;
    }

    public IReadOnlyCollection<string> Names => [.. _consumers.Keys.Select(c => c.Name).Distinct(StringComparer.Ordinal)];

    public bool IsPaused(string name) => _switches.IsOn(ConsumerSwitches.Paused(name));

    public IDisposable Register(IPausableConsumer consumer)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer.Name, nameof(consumer));
        var entry = new Entry(consumer);
        if (!_consumers.TryAdd(consumer, entry))
        {
            throw new InvalidOperationException($"The consumer '{consumer.Name}' is already registered.");
        }

        // A switch turned on before the consumer existed (or while it registered) applies now.
        entry.Last = Apply(entry);
        return new Registration(this, consumer);
    }

    /// <summary>Waits until every pending pause or resume has been applied, for tests.</summary>
    internal Task WhenAppliedAsync() => Task.WhenAll(_consumers.Values.Select(e => e.Last));

    public void Dispose() => _switches.Changed -= OnSwitchChanged;

    private void OnSwitchChanged(object? sender, SwitchChangedEventArgs e)
    {
        foreach (var entry in _consumers.Values)
        {
            if (string.Equals(entry.SwitchName, e.Name, StringComparison.Ordinal))
            {
                entry.Last = Apply(entry);
            }
        }
    }

    /// <summary>
    /// Brings the consumer in line with its switch. Changes are serialized per consumer and read the switch again, so
    /// concurrent or out-of-order events end in the switch's latest state; nothing is called when the state is already right.
    /// </summary>
    private async Task Apply(Entry entry)
    {
        // Never on the caller's thread: switches change on the engine loop, and a consumer may block before awaiting.
        await Task.Yield();

        var failed = false;
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var consumer = entry.Consumer;
            var paused = _switches.IsOn(entry.SwitchName);
            if (paused == entry.Paused || !_consumers.ContainsKey(consumer))
            {
                entry.Failures = 0;
                return;
            }

            // A previous call that timed out may still be running: let it finish first (bounded), so it cannot land
            // after this one and undo it (a stop completing after the resume).
            if (entry.InFlight is { IsCompleted: false } previous)
            {
                using var drain = new CancellationTokenSource(CallTimeout, _timeProvider);
                try
                {
                    await previous.WaitAsync(drain.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (drain.IsCancellationRequested)
                {
                    throw new TimeoutException($"The previous pause or resume of consumer '{consumer.Name}' is still running.");
                }
                catch (Exception)
                {
                    // Its outcome no longer matters: this call sets the state the switch asks for.
                }
            }

            using var timeout = new CancellationTokenSource(CallTimeout, _timeProvider);
            var token = timeout.Token;
            Task call;

            // Started on its own task: a consumer that blocks before returning its task would otherwise hold this
            // consumer's gate forever, beyond the timeout, and no later change could ever reach it.
            if (paused)
            {
                _logger.LogWarning("Pausing consumer {Consumer}.", consumer.Name);
                call = Task.Run(() => consumer.PauseAsync(token), CancellationToken.None);
            }
            else
            {
                _logger.LogInformation("Resuming consumer {Consumer}.", consumer.Name);
                call = Task.Run(() => consumer.ResumeAsync(token), CancellationToken.None);
            }

            entry.InFlight = call;
            await call.WaitAsync(timeout.Token).ConfigureAwait(false);

            entry.Paused = paused;
            entry.Failures = 0;
        }
        catch (Exception ex)
        {
            // A call that failed or timed out may still take effect later (a stop waiting for a stuck handler): the
            // consumer's state is unknown, so the next change always calls the method that matches the switch.
            entry.Paused = null;
            failed = true;
            var delay = RetryDelays[Math.Min(entry.Failures, RetryDelays.Length - 1)];
            entry.Failures++;
            _logger.LogError(ex, "Pausing or resuming consumer {Consumer} failed; retrying in {Delay}.", entry.Consumer.Name, delay);
        }
        finally
        {
            entry.Gate.Release();
        }

        // Retried until the consumer matches its switch (which may have flipped back meanwhile: then nothing to do). One
        // retry at a time per consumer, however many changes failed.
        if (failed && Interlocked.Exchange(ref entry.RetryScheduled, 1) == 0)
        {
            await Task.Delay(RetryDelays[Math.Min(entry.Failures - 1, RetryDelays.Length - 1)], _timeProvider).ConfigureAwait(false);
            Volatile.Write(ref entry.RetryScheduled, 0);
            if (_consumers.ContainsKey(entry.Consumer))
            {
                entry.Last = Apply(entry);
            }
        }
    }

    private sealed class Entry(IPausableConsumer consumer)
    {
        public IPausableConsumer Consumer { get; } = consumer;

        /// <summary>The switch that pauses it, named once (looked for on every switch change).</summary>
        public string SwitchName { get; } = ConsumerSwitches.Paused(consumer.Name);

        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>
        /// The state applied to the consumer: a new consumer is running; <c>null</c> after a call that failed or timed
        /// out, whose effect is unknown.
        /// </summary>
        public bool? Paused { get; set; } = false;

        public Task Last { get; set; } = Task.CompletedTask;

        /// <summary>The latest pause or resume call, which may still run after its wait timed out.</summary>
        public Task? InFlight { get; set; }

        public int Failures { get; set; }

        public int RetryScheduled;
    }

    private sealed class Registration(HomeostatConsumers owner, IPausableConsumer consumer) : IDisposable
    {
        public void Dispose() => owner._consumers.TryRemove(consumer, out _);
    }
}
