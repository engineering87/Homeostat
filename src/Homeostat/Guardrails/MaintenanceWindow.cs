namespace Homeostat.Guardrails;

/// <summary>
/// A recurring period during which Homeostat behaves as paused: conditions are tracked, no plan starts and runs in
/// progress stop before their next step. Configure it with <see cref="HomeostatBuilder.AddMaintenanceWindow"/>.
/// </summary>
public sealed class MaintenanceWindow
{
    private readonly HashSet<DayOfWeek> _days = [];

    internal MaintenanceWindow(string name) => Name = name;

    /// <summary>Name shown in logs and in the pause reason.</summary>
    public string Name { get; }

    /// <summary>Local start time of the window.</summary>
    public TimeOnly Start { get; private set; }

    /// <summary>Length of the window; it can cross midnight.</summary>
    public TimeSpan Duration { get; private set; }

    /// <summary>Days on which the window starts; empty means every day.</summary>
    public IReadOnlyCollection<DayOfWeek> Days => _days;

    /// <summary>Time zone of <see cref="Start"/>; UTC by default.</summary>
    public TimeZoneInfo TimeZone { get; private set; } = TimeZoneInfo.Utc;

    /// <summary>Every day from <paramref name="start"/> for <paramref name="duration"/>.</summary>
    /// <param name="start">Local start time.</param>
    /// <param name="duration">Length, more than zero and less than a week.</param>
    public MaintenanceWindow Daily(TimeOnly start, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration >= TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "The duration must be positive and shorter than a week.");
        }

        Start = start;
        Duration = duration;
        return this;
    }

    /// <summary>Restricts the window to start only on the given days.</summary>
    /// <param name="days">Days of the week.</param>
    public MaintenanceWindow On(params DayOfWeek[] days)
    {
        ArgumentNullException.ThrowIfNull(days);
        foreach (var day in days)
        {
            _days.Add(day);
        }

        return this;
    }

    /// <summary>Interprets <see cref="Start"/> in the given time zone (daylight saving time included).</summary>
    /// <param name="timeZone">The time zone, for example <c>TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome")</c>.</param>
    public MaintenanceWindow InTimeZone(TimeZoneInfo timeZone)
    {
        TimeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        return this;
    }

    /// <summary>Whether the window is open at <paramref name="now"/>.</summary>
    /// <param name="now">The time to test.</param>
    public bool IsActive(DateTimeOffset now)
    {
        if (Duration <= TimeSpan.Zero)
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTime(now, TimeZone);

        // A window that started on one of the previous days may still be open.
        for (var daysBack = 0; daysBack <= (int)Math.Ceiling(Duration.TotalDays); daysBack++)
        {
            var date = DateOnly.FromDateTime(local.DateTime).AddDays(-daysBack);
            if (_days.Count > 0 && !_days.Contains(date.DayOfWeek))
            {
                continue;
            }

            var startLocal = date.ToDateTime(Start);
            var start = new DateTimeOffset(startLocal, TimeZone.GetUtcOffset(startLocal));
            if (now >= start && now < start + Duration)
            {
                return true;
            }
        }

        return false;
    }
}
