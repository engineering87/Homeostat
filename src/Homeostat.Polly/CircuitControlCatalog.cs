using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace Homeostat.Polly;

/// <summary>
/// Manual controls of circuit breakers, keyed by name, each driven by the Homeostat switch
/// <c>circuit:{name}:isolated</c>: turning the switch on isolates every circuit breaker attached to the control,
/// turning it off closes them.
/// </summary>
internal sealed class CircuitControlCatalog : IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _controls = new(StringComparer.Ordinal);
    private readonly IHomeostatSwitches _switches;
    private readonly ILogger<CircuitControlCatalog> _logger;

    public CircuitControlCatalog(IHomeostatSwitches switches, ILogger<CircuitControlCatalog> logger)
    {
        _switches = switches;
        _logger = logger;
        _switches.Changed += OnSwitchChanged;
    }

    public static string SwitchName(string control) => $"circuit:{control}:isolated";

    public CircuitBreakerManualControl GetOrAdd(string name)
    {
        if (_controls.TryGetValue(name, out var existing))
        {
            return existing.Control;
        }

        var isolated = _switches.IsOn(SwitchName(name));
        var created = new Entry(new CircuitBreakerManualControl(isolated));
        var entry = _controls.GetOrAdd(name, created);
        if (ReferenceEquals(entry, created) && _switches.IsOn(SwitchName(name)) != isolated)
        {
            // The switch changed between reading it and publishing the entry: that event found no entry to update.
            _ = ApplyAsync(name, entry);
        }

        return entry.Control;
    }

    public IEnumerable<string> Names => _controls.Keys;

    public void Dispose() => _switches.Changed -= OnSwitchChanged;

    private void OnSwitchChanged(object? sender, SwitchChangedEventArgs e)
    {
        foreach (var (name, entry) in _controls)
        {
            if (string.Equals(SwitchName(name), e.Name, StringComparison.Ordinal))
            {
                _ = ApplyAsync(name, entry);
            }
        }
    }

    /// <summary>
    /// Brings the control in line with the current state of its switch. Changes are serialized per control and always
    /// read the switch again, so concurrent or out-of-order events still end in the switch's latest state.
    /// </summary>
    private async Task ApplyAsync(string name, Entry entry)
    {
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_switches.IsOn(SwitchName(name)))
            {
                _logger.LogWarning("Isolating circuit breakers controlled by {Control}.", name);
                await entry.Control.IsolateAsync().ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Closing circuit breakers controlled by {Control}.", name);
                await entry.Control.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Changing the circuit breakers controlled by {Control} failed.", name);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private sealed class Entry(CircuitBreakerManualControl control)
    {
        public CircuitBreakerManualControl Control { get; } = control;

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}
