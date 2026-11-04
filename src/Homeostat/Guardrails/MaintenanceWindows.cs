namespace Homeostat.Guardrails;

/// <summary>All configured maintenance windows.</summary>
internal sealed class MaintenanceWindows(IEnumerable<MaintenanceWindow> windows)
{
    private readonly MaintenanceWindow[] _windows = windows.ToArray();

    public string? ActiveWindow(DateTimeOffset now)
    {
        foreach (var window in _windows)
        {
            if (window.IsActive(now))
            {
                return window.Name;
            }
        }

        return null;
    }

    public bool IsActive(DateTimeOffset now) => ActiveWindow(now) is not null;
}
