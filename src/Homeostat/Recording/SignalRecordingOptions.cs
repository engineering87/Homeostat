namespace Homeostat.Recording;

/// <summary>Settings of the signal recording enabled with <c>AddSignalRecording</c>.</summary>
public sealed class SignalRecordingOptions
{
    /// <summary>
    /// File path. <c>{date}</c> is replaced with the UTC date of each signal (<c>yyyy-MM-dd</c>), so there is one file per
    /// day; <c>{instance}</c> with <see cref="HomeostatOptions.InstanceId"/>, so instances sharing a volume do not write
    /// to the same file. Defaults to <c>signals/homeostat-signals-{instance}-{date}.jsonl</c>.
    /// </summary>
    public string Path { get; set; } = "signals/homeostat-signals-{instance}-{date}.jsonl";

    /// <summary>Sources to record; empty (the default) records every source.</summary>
    public ISet<string> Sources { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// How long daily files are kept (rounded up to whole days); older ones are deleted. Defaults to 14 days;
    /// <see cref="TimeSpan.Zero"/> keeps everything.
    /// </summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Whether to record signal messages and tags. They can carry details from external systems (error messages, host
    /// names, alert annotations), so they are left out by default; values, severities and sources, which conditions
    /// evaluate, are always recorded. Defaults to <c>false</c>.
    /// </summary>
    public bool IncludeDetails { get; set; }

    /// <summary>Signals buffered in memory while the disk is slow; the oldest are dropped beyond it. Defaults to 10,000.</summary>
    public int BufferCapacity { get; set; } = 10_000;
}
