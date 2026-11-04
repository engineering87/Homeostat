namespace Homeostat.Detectors;

/// <summary>Settings of <c>AddBackgroundServiceSignals</c>.</summary>
public sealed class BackgroundServiceSignalOptions
{
    /// <summary>Source prefix: each service publishes <c>{prefix}:{type name}</c>. Defaults to <c>"hosted-service"</c>.</summary>
    public string SourcePrefix { get; set; } = "hosted-service";
}
