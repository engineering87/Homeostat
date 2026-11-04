using Homeostat.Signals;

namespace Homeostat.Metrics;

/// <summary>Settings of a signal read from an EventCounter (see <c>AddEventCounterSignal</c>).</summary>
public sealed class EventCounterSignalOptions
{
    /// <summary>Signal source; defaults to <c>counter:{event source}:{counter}</c>.</summary>
    public string? Source { get; set; }

    /// <summary>
    /// How often the counter is reported, in whole seconds from 1 second to 1 hour; defaults to 10 seconds. The event
    /// source reports at the shortest interval
    /// any listener asks for (another registration on the same source, dotnet-counters, a telemetry agent), so signals
    /// can come more often. Incrementing counters (connections opened, requests) are published as a rate per second,
    /// whatever the interval.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Turns the counter value into the signal value, for example a share of a maximum.</summary>
    public Func<double, double>? Transform { get; set; }

    /// <summary>The signal is Degraded when the value is above this threshold.</summary>
    public double? DegradedAbove { get; set; }

    /// <summary>The signal is Unhealthy when the value is above this threshold.</summary>
    public double? UnhealthyAbove { get; set; }

    /// <summary>The signal is Degraded when the value is below this threshold.</summary>
    public double? DegradedBelow { get; set; }

    /// <summary>The signal is Unhealthy when the value is below this threshold.</summary>
    public double? UnhealthyBelow { get; set; }

    internal SignalSeverity SeverityOf(double value)
    {
        if (value > UnhealthyAbove || value < UnhealthyBelow)
        {
            return SignalSeverity.Unhealthy;
        }

        return value > DegradedAbove || value < DegradedBelow ? SignalSeverity.Degraded : SignalSeverity.Healthy;
    }
}

internal sealed record EventCounterRegistration(string EventSource, string Counter, string Source, EventCounterSignalOptions Options);
