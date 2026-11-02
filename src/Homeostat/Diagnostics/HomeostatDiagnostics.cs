using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Homeostat.Diagnostics;

/// <summary>
/// Telemetry names. With OpenTelemetry, subscribe with
/// <c>.AddSource(HomeostatDiagnostics.Name)</c> and <c>.AddMeter(HomeostatDiagnostics.Name)</c>.
/// </summary>
public static class HomeostatDiagnostics
{
    /// <summary>Name of the activity source and of the meter.</summary>
    public const string Name = "Homeostat";

    internal static readonly ActivitySource ActivitySource = new(Name);

    internal static readonly Meter Meter = new(Name);

    /// <summary>Healing runs started, tagged with <c>condition</c>.</summary>
    internal static readonly Counter<long> Runs =
        Meter.CreateCounter<long>("homeostat.healing.runs", description: "Healing runs started.");

    /// <summary>Actions executed or simulated, tagged with <c>condition</c>, <c>action</c>, <c>dry_run</c>.</summary>
    internal static readonly Counter<long> Actions =
        Meter.CreateCounter<long>("homeostat.healing.actions", description: "Remediation actions executed or simulated.");

    /// <summary>Run results, tagged with <c>condition</c> and <c>outcome</c>.</summary>
    internal static readonly Counter<long> Outcomes =
        Meter.CreateCounter<long>("homeostat.healing.outcomes", description: "Healing run results.");

    /// <summary>Run duration in seconds, tagged with <c>condition</c> and <c>outcome</c>.</summary>
    internal static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("homeostat.healing.duration", unit: "s", description: "Duration of healing runs.");
}
