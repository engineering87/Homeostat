namespace Homeostat.Samples.Architectures.ModularMonolith;

/// <summary>The forecast provider client of the forecasts module (your code).</summary>
public interface IForecastProvider
{
    /// <summary>Calls the provider's status endpoint; throws when it does not answer.</summary>
    Task PingAsync(CancellationToken cancellationToken);
}

/// <summary>The forecasts store, written by the forecasts module and read by the others (your code).</summary>
public interface IForecastStore
{
    /// <summary>Round trip to the store; throws when it cannot be reached.</summary>
    Task PingAsync(CancellationToken cancellationToken);

    /// <summary>Closes the store's connections and opens new ones.</summary>
    Task ReopenAsync(CancellationToken cancellationToken);
}

/// <summary>The gateway the alerts module sends its alerts through (your code).</summary>
public interface IAlertGateway
{
    /// <summary>Round trip to the primary gateway; throws when it does not answer.</summary>
    Task PingAsync(CancellationToken cancellationToken);
}

/// <summary>How the alerts module reads the forecasts store, with its own connections (your code).</summary>
public interface IAlertsForecastReader
{
    /// <summary>A small read from the store; throws when it fails.</summary>
    Task PingAsync(CancellationToken cancellationToken);
}

/// <summary>The database of the reports module (your code).</summary>
public interface IReportsDatabase
{
    /// <summary>Round trip to the database; throws when it cannot be reached.</summary>
    Task PingAsync(CancellationToken cancellationToken);

    /// <summary>Refreshes the statistics the export queries are planned with; takes minutes.</summary>
    Task RefreshStatisticsAsync(CancellationToken cancellationToken);
}

/// <summary>How the reports module reads the forecasts store, with its own connections (your code).</summary>
public interface IReportsForecastReader
{
    /// <summary>A small read from the store; throws when it fails.</summary>
    Task PingAsync(CancellationToken cancellationToken);

    /// <summary>Closes the module's connections to the store and opens new ones.</summary>
    Task ResetConnectionsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// How the reports module reads the switch of the process-wide plan (your code): new exports wait while it is on.
/// </summary>
public sealed class ReportExports(IHomeostatSwitches switches)
{
    /// <summary>Whether a new export request starts now, or waits in the queue.</summary>
    public bool StartsNewExports => !switches.IsOn(ReportsHealing.Paused);
}

/// <summary>
/// The tiles module (your code). It has no condition of its own: it only reads the switch of the process-wide plan.
/// </summary>
public sealed class TileRenderer(IHomeostatSwitches switches)
{
    /// <summary>Render map tiles at a lower resolution, with less CPU.</summary>
    public const string LowResolution = "tiles-low-resolution";

    /// <summary>Pixels per side of a rendered tile.</summary>
    public int TileSize => switches.IsOn(LowResolution) ? 256 : 512;
}

/// <summary>
/// What a person uses during an incident in one module (your code, for example behind an operations endpoint): the
/// module's healing stops, the other modules keep theirs.
/// </summary>
public sealed class ModuleIncidents(IHomeostatControl control, TimeProvider time)
{
    /// <summary>Stops the healing of one module, for a limited time.</summary>
    public ValueTask PauseHealingAsync(string module, TimeSpan duration, string by, CancellationToken cancellationToken = default)
        // A timed pause ends by itself: a forgotten resume does not leave the module without healing.
        => control.PauseGroupAsync(module, until: time.GetUtcNow() + duration, by: by, cancellationToken: cancellationToken);

    /// <summary>Gives the module its healing back.</summary>
    public ValueTask ResumeHealingAsync(string module, string by, CancellationToken cancellationToken = default)
        => control.ResumeGroupAsync(module, by: by, cancellationToken: cancellationToken);
}
