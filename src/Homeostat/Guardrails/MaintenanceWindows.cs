namespace Homeostat.Guardrails;

/// <summary>All configured maintenance windows.</summary>
internal sealed class MaintenanceWindows(IEnumerable<MaintenanceWindow> windows)
{
    private readonly MaintenanceWindow[] _windows = windows.ToArray();

    public string? ActiveWindow(DateTimeOffset now) => _windows.FirstOrDefault(w => w.IsActive(now))?.Name;
}
