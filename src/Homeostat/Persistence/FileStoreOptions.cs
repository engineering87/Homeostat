namespace Homeostat.Persistence;

/// <summary>Settings of <see cref="FileHomeostatStore"/>.</summary>
public sealed class FileStoreOptions
{
    /// <summary>
    /// How long the cooldown and backoff state of a plan that no longer runs is kept: older entries (renamed
    /// conditions, former instance names) are dropped when the file is loaded, so the file does not grow forever.
    /// Plans that are held, or that never completed a run, are always kept. Defaults to 30 days.
    /// </summary>
    public TimeSpan PlanStateRetention { get; set; } = TimeSpan.FromDays(30);
}
