using System.Globalization;

namespace Homeostat.Conditions;

/// <summary>
/// The cycle a metric follows, for the seasonal rules (<see cref="ConditionBuilder.WhenSeasonallyHigh"/>): each value is
/// compared with the values seen at the same time of the day (<see cref="Daily"/>) or of the week (<see cref="Weekly"/>)
/// in earlier periods, not with the last hour.
/// </summary>
public sealed class Seasonality
{
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private Seasonality(TimeSpan period, TimeSpan bucket, TimeZoneInfo timeZone)
    {
        if (bucket < TimeSpan.FromMinutes(1) || bucket.Ticks % TimeSpan.TicksPerMinute != 0 || period.Ticks % bucket.Ticks != 0
            || bucket.Ticks * 2 > period.Ticks || period.Ticks / bucket.Ticks > MaxBuckets)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bucket), bucket,
                $"The bucket must be a whole number of minutes that divides the period exactly, with between 2 and {MaxBuckets} buckets per period (for example 15 minutes for a day, 1 hour for a week; at least 5 minutes for a week).");
        }

        Period = period;
        Bucket = bucket;
        TimeZone = timeZone;
    }

    /// <summary>Most time slots in a period (a week of 5-minute slots), which keeps the saved baseline small.</summary>
    internal const int MaxBuckets = 2016;

    /// <summary>Length of the cycle: one day or one week.</summary>
    public TimeSpan Period { get; }

    /// <summary>Width of the time slots compared with each other, for example 15 minutes.</summary>
    public TimeSpan Bucket { get; }

    /// <summary>Time zone whose clock the cycle follows (business hours, daylight saving time).</summary>
    public TimeZoneInfo TimeZone { get; }

    /// <summary>
    /// A daily cycle: 10:15 today is compared with 10:15 on earlier days. For metrics that follow the hours of the day
    /// but not the day of the week.
    /// </summary>
    /// <param name="bucket">Width of the time slots; defaults to 15 minutes.</param>
    /// <param name="timeZone">Time zone of the cycle; defaults to UTC. Use the users' time zone when the load follows them.</param>
    public static Seasonality Daily(TimeSpan? bucket = null, TimeZoneInfo? timeZone = null)
        => new(Day, bucket ?? TimeSpan.FromMinutes(15), timeZone ?? TimeZoneInfo.Utc);

    /// <summary>
    /// A weekly cycle: Monday 10:00 is compared with earlier Mondays at 10:00. For metrics that differ between weekdays
    /// and weekends; it needs more weeks of history to learn.
    /// </summary>
    /// <param name="bucket">Width of the time slots; defaults to 1 hour.</param>
    /// <param name="timeZone">Time zone of the cycle; defaults to UTC.</param>
    public static Seasonality Weekly(TimeSpan? bucket = null, TimeZoneInfo? timeZone = null)
        => new(Week, bucket ?? TimeSpan.FromHours(1), timeZone ?? TimeZoneInfo.Utc);

    /// <summary>Number of time slots in a period.</summary>
    internal int Buckets => (int)(Period.Ticks / Bucket.Ticks);

    /// <summary>Identifies the cycle, so a baseline learned with another cycle is never reused.</summary>
    internal string Signature => string.Create(
        CultureInfo.InvariantCulture, $"{(Period == Week ? "weekly" : "daily")}-{(long)Bucket.TotalMinutes}m-{ZoneId(TimeZone)}");

    /// <summary>The IANA id of a zone, whichever spelling was used ("W. Europe Standard Time" or "Europe/Rome" style).</summary>
    private static string ZoneId(TimeZoneInfo zone)
        => !zone.HasIanaId && TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;

    /// <summary>The time slot of an instant, and the index of its period (days or weeks since year 1, in the time zone).</summary>
    internal (int Bucket, long Period) Locate(DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, TimeZone).DateTime;
        var day = DateOnly.FromDateTime(local).DayNumber; // Day 0, January 1st of year 1, was a Monday.
        var within = Period == Week ? TimeSpan.FromDays(day % 7) + local.TimeOfDay : local.TimeOfDay;
        var period = Period == Week ? day / 7 : day;
        return ((int)Math.Min(within.Ticks / Bucket.Ticks, Buckets - 1), period);
    }

    /// <inheritdoc />
    public override string ToString() => Signature;
}
