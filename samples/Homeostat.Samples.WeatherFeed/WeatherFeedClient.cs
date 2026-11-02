namespace Homeostat.Samples.WeatherFeed;

/// <summary>
/// Simulates a client for a weather data feed with a primary and a backup provider.
/// The client can get stuck; renewing it fixes transient faults, while an outage of the
/// primary provider requires switching to the backup.
/// </summary>
public sealed class WeatherFeedClient
{
    private readonly object _gate = new();
    private bool _stuck;
    private bool _primaryOutage;
    private bool _usingBackup;
    private int _renewals;

    public bool IsStuck { get { lock (_gate) { return _stuck; } } }

    public bool UsingBackup { get { lock (_gate) { return _usingBackup; } } }

    public int Renewals { get { lock (_gate) { return _renewals; } } }

    public string ActiveProvider => UsingBackup ? "backup" : "primary";

    /// <summary>The client stops responding; renewing it is enough.</summary>
    public void SimulateStuckClient()
    {
        lock (_gate) { _stuck = true; }
    }

    /// <summary>The primary provider goes down; renewing the client does not help.</summary>
    public void SimulatePrimaryOutage()
    {
        lock (_gate) { _stuck = true; _primaryOutage = true; }
    }

    public void Renew()
    {
        lock (_gate)
        {
            _renewals++;
            if (!_primaryOutage || _usingBackup)
            {
                _stuck = false;
            }
        }
    }

    public void SwitchToBackup()
    {
        lock (_gate) { _usingBackup = true; _stuck = false; }
    }

    public void Restore()
    {
        lock (_gate) { _stuck = false; _primaryOutage = false; _usingBackup = false; }
    }
}
