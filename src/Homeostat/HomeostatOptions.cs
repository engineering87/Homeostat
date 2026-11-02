namespace Homeostat;

/// <summary>
/// Global settings and guardrails. The defaults are deliberately conservative.
/// </summary>
public sealed class HomeostatOptions
{
    /// <summary>
    /// When <c>true</c> (the default), Homeostat detects conditions and logs which actions it
    /// would run, without running them. Switch it off once the logs show the expected decisions.
    /// </summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Maximum number of remediation actions executed across all plans within <see cref="ActionBudgetWindow"/>.</summary>
    public int MaxActionsPerWindow { get; set; } = 10;

    /// <summary>Sliding window used by <see cref="MaxActionsPerWindow"/>.</summary>
    public TimeSpan ActionBudgetWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Maximum number of healing runs executing at the same time.</summary>
    public int MaxConcurrentHealings { get; set; } = 1;

    /// <summary>Default time to wait after a healing run before the same plan can run again.</summary>
    public TimeSpan DefaultCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Default maximum duration of a single remediation action.</summary>
    public TimeSpan DefaultActionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Default time allowed for verification after an action.</summary>
    public TimeSpan DefaultVerificationTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the built-in verifier checks whether the condition has cleared.</summary>
    public TimeSpan VerificationPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Capacity of the in-memory signal buffer. When full, the oldest signals are dropped.</summary>
    public int SignalBufferCapacity { get; set; } = 1024;

    /// <summary>Default time to wait for a person to approve a step marked with <c>RequireApproval</c>.</summary>
    public TimeSpan DefaultApprovalTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How many completed runs the store keeps (see <see cref="IHomeostatControl.GetRecentRunsAsync"/>).</summary>
    public int RunHistorySize { get; set; } = 50;

    /// <summary>
    /// How many condition events (a condition becoming active or clearing) the history keeps (see
    /// <see cref="IHomeostatControl.GetConditionHistoryAsync"/>). The file and Redis stores keep it, so it survives
    /// restarts and, with a shared store, spans every instance; with the in-memory store or a store that does not support
    /// it, the history covers this process. Defaults to 200; 0 disables the history.
    /// </summary>
    public int ConditionHistorySize { get; set; } = 200;

    /// <summary>
    /// Identifies this instance in run records, leases and the keys of instance-scoped plan state. Defaults to the
    /// machine name, which is stable across restarts and, in containers, is the pod or container name. Set it
    /// explicitly when several processes on the same machine share a store.
    /// </summary>
    public string InstanceId { get; set; } = Environment.MachineName;

    /// <summary>
    /// Duration of the lease taken by plans marked with <c>OncePerCluster()</c>. The lease is renewed while the run
    /// lasts; if the instance dies, another can take over once it expires.
    /// </summary>
    public TimeSpan ClusterLeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Name of the application, included in notifications and audit records.</summary>
    public string ApplicationName { get; set; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "application";

    /// <summary>How often detectors (thread pool, memory, exceptions, HTTP clients, ...) take a sample.</summary>
    public TimeSpan DetectionInterval { get; set; } = TimeSpan.FromSeconds(10);
}
