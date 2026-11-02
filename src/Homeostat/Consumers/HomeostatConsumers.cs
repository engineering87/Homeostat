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
            if (string.Equals(ConsumerSwitches.Paused(entry.Consumer.Name), e.Name, StringComparison.Ordinal))
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
            var paused = _switches.IsOn(ConsumerSwitches.Paused(consumer.Name));
            if (paused == entry.Paused || !_consumers.ContainsKey(consumer))
            {
                entry.Failures = 0;
                return;
            }

            using var timeout = new CancellationTokenSource(CallTimeout, _timeProvider);
            if (paused)
            {
                _logger.LogWarning("Pausing consumer {Consumer}.", consumer.Name);
                await consumer.PauseAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Resuming consumer {Consumer}.", consumer.Name);
                await consumer.ResumeAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            }

            entry.Paused = paused;
            entry.Failures = 0;
        }
        catch (Exception ex)
        {
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

        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// <summary>The state applied to the consumer: a new consumer is running.</summary>
        public bool Paused { get; set; }

        public Task Last { get; set; } = Task.CompletedTask;

        public int Failures { get; set; }

        public int RetryScheduled;
    }

    private sealed class Registration(HomeostatConsumers owner, IPausableConsumer consumer) : IDisposable
    {
        public void Dispose() => owner._consumers.TryRemove(consumer, out _);
    }
}
