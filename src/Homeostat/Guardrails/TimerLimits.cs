namespace Homeostat.Guardrails;

/// <summary>Upper bound accepted by .NET timers (about 49.7 days).</summary>
internal static class TimerLimits
{
    public static readonly TimeSpan Max = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    public static bool IsValid(TimeSpan value) => value > TimeSpan.Zero && value <= Max;

    /// <summary>The value, at most <see cref="Max"/>: for delays computed from a valid setting (a multiple of it).</summary>
    public static TimeSpan Clamp(TimeSpan value) => value > Max ? Max : value;
}
