using Homeostat.Signals;

namespace Homeostat.Probes;

/// <summary>Settings of a probe registered with <c>AddProbe</c> or <c>AddLatencyProbe</c>.</summary>
public class ProbeOptions
{
    /// <summary>
    /// How often the probe runs. <c>null</c> (the default) runs it at every detection interval
    /// (<see cref="HomeostatOptions.DetectionInterval"/>); a longer value skips detection ticks.
    /// </summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>
    /// How long a probe call may take. A call that does not complete in time is published as a failure
    /// with the tag <c>outcome=timeout</c>. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The value is published as <see cref="SignalSeverity.Degraded"/> when it is above this threshold.</summary>
    public double? DegradedAbove { get; set; }

    /// <summary>The value is published as <see cref="SignalSeverity.Unhealthy"/> when it is above this threshold.</summary>
    public double? UnhealthyAbove { get; set; }

    /// <summary>The value is published as <see cref="SignalSeverity.Degraded"/> when it is below this threshold.</summary>
    public double? DegradedBelow { get; set; }

    /// <summary>The value is published as <see cref="SignalSeverity.Unhealthy"/> when it is below this threshold.</summary>
    public double? UnhealthyBelow { get; set; }

    /// <summary>Severity published when the probe throws or times out. Defaults to <see cref="SignalSeverity.Unhealthy"/>.</summary>
    public SignalSeverity FailureSeverity { get; set; } = SignalSeverity.Unhealthy;

    /// <summary>
    /// Value of the <c>origin</c> tag of the published signals. Defaults to <c>probe</c> for <c>AddProbe</c> and
    /// <c>latency-probe</c> for <c>AddLatencyProbe</c>; integration packages set their own.
    /// </summary>
    public string? Origin { get; set; }

    internal void Validate(string source)
    {
        if (Interval is { } interval && !Guardrails.TimerLimits.IsValid(interval))
        {
            throw new ArgumentException($"The interval of probe '{source}' must be positive and at most about 49 days.");
        }

        if (!Guardrails.TimerLimits.IsValid(Timeout))
        {
            throw new ArgumentException($"The timeout of probe '{source}' must be positive and at most about 49 days.");
        }

        if (!Enum.IsDefined(FailureSeverity))
        {
            throw new ArgumentException($"Probe '{source}': FailureSeverity is not a valid severity.");
        }

        if (Origin is not null && string.IsNullOrWhiteSpace(Origin))
        {
            throw new ArgumentException($"Probe '{source}': Origin cannot be empty.");
        }

        foreach (var threshold in new[] { DegradedAbove, UnhealthyAbove, DegradedBelow, UnhealthyBelow })
        {
            if (threshold is { } t && !double.IsFinite(t))
            {
                throw new ArgumentException($"The thresholds of probe '{source}' must be finite numbers.");
            }
        }

        if (DegradedAbove > UnhealthyAbove)
        {
            throw new ArgumentException($"Probe '{source}': DegradedAbove cannot be greater than UnhealthyAbove.");
        }

        if (DegradedBelow < UnhealthyBelow)
        {
            throw new ArgumentException($"Probe '{source}': DegradedBelow cannot be less than UnhealthyBelow.");
        }
    }

    internal SignalSeverity Classify(double value)
    {
        if (value > UnhealthyAbove || value < UnhealthyBelow)
        {
            return SignalSeverity.Unhealthy;
        }

        if (value > DegradedAbove || value < DegradedBelow)
        {
            return SignalSeverity.Degraded;
        }

        return SignalSeverity.Healthy;
    }

    /// <summary>
    /// Copies the probe settings of this instance to <paramref name="target"/>. Integration packages whose options
    /// derive from <see cref="ProbeOptions"/> use it to register a probe with their settings.
    /// </summary>
    /// <param name="target">The settings to overwrite.</param>
    public void CopyTo(ProbeOptions target)
    {
        target.Interval = Interval;
        target.Timeout = Timeout;
        target.DegradedAbove = DegradedAbove;
        target.UnhealthyAbove = UnhealthyAbove;
        target.DegradedBelow = DegradedBelow;
        target.UnhealthyBelow = UnhealthyBelow;
        target.FailureSeverity = FailureSeverity;
        target.Origin = Origin;
    }
}
