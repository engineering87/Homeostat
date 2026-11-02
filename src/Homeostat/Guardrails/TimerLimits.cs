namespace Homeostat.Guardrails;

/// <summary>Upper bound accepted by .NET timers (about 49.7 days).</summary>
internal static class TimerLimits
{
    public static readonly TimeSpan Max = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    public static bool IsValid(TimeSpan value) => value > TimeSpan.Zero && value <= Max;
}
