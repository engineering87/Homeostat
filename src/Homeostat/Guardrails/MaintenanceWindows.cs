namespace Homeostat.Guardrails;

/// <summary>All configured maintenance windows: the global ones, and those of healing groups (<see cref="MaintenanceWindow.ForGroups"/>).</summary>
internal sealed class MaintenanceWindows(IEnumerable<MaintenanceWindow> windows)
{
    private readonly MaintenanceWindow[] _global = windows.Where(w => w.Groups.Count == 0).ToArray();
    private readonly MaintenanceWindow[] _grouped = windows.Where(w => w.Groups.Count > 0).ToArray();

    /// <summary>The open window that pauses every plan, or <c>null</c>.</summary>
    public string? ActiveWindow(DateTimeOffset now) => First(_global, now, group: null);

    public bool IsActive(DateTimeOffset now) => ActiveWindow(now) is not null;

    /// <summary>The open window of <paramref name="group"/> (not counting the global ones), or <c>null</c>.</summary>
    public string? ActiveGroupWindow(DateTimeOffset now, string group) => _grouped.Length == 0 ? null : First(_grouped, now, group);

    private static string? First(MaintenanceWindow[] windows, DateTimeOffset now, string? group)
    {
        foreach (var window in windows)
        {
            if ((group is null || window.Groups.Contains(group, StringComparer.Ordinal)) && window.IsActive(now))
            {
                return window.Name;
            }
        }

        return null;
    }
}
