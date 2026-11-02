namespace Homeostat.Testing;

/// <summary>Settings for <see cref="HomeostatTestHost"/>.</summary>
public sealed class HomeostatTestHostOptions
{
    /// <summary>Dry-run setting of the host. Tests usually want real actions, so the default is <c>false</c>.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// How much simulated time passes at each tick. Ticks run in the background from the moment the host starts.
    /// <c>null</c> disables automatic advancing: move time yourself with <see cref="HomeostatTestHost.Time"/>.
    /// Defaults to 1 second.
    /// </summary>
    public TimeSpan? AutoAdvanceStep { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Real time between two automatic ticks. Defaults to 5 milliseconds.</summary>
    public TimeSpan AutoAdvanceInterval { get; set; } = TimeSpan.FromMilliseconds(5);

    /// <summary>Real time after which waiting methods give up with a <see cref="TimeoutException"/>. Defaults to 10 seconds.</summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Initial simulated time. Defaults to 2030-01-01 00:00 UTC.</summary>
    public DateTimeOffset StartTime { get; set; } = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
}
