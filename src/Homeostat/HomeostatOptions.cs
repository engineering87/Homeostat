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

    /// <summary>Maximum number of healing steps executed across all plans within <see cref="ActionBudgetWindow"/>; when it is spent, runs stop with <c>BudgetExhausted</c>. Defaults to 10.</summary>
    public int MaxActionsPerWindow { get; set; } = 10;

    /// <summary>Sliding window used by <see cref="MaxActionsPerWindow"/>. Defaults to 1 hour.</summary>
    public TimeSpan ActionBudgetWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Maximum number of healing runs executing at the same time, across all conditions. Defaults to 1.</summary>
    public int MaxConcurrentHealings { get; set; } = 1;

    /// <summary>Time to wait after a healing run before the same plan can run again, unless the plan sets <c>Cooldown</c>. Defaults to 5 minutes.</summary>
    public TimeSpan DefaultCooldown { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum duration of a single healing step, unless the plan sets <c>ActionTimeout</c>; a step that takes longer is canceled and counts as failed. Defaults to 30 seconds.</summary>
    public TimeSpan DefaultActionTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Time allowed for verification after a step, unless the plan sets <c>VerificationTimeout</c>; when it expires without evidence the outcome is inconclusive. Defaults to 1 minute.</summary>
    public TimeSpan DefaultVerificationTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often the built-in verifier checks whether the condition has cleared. Defaults to 1 second.</summary>
    public TimeSpan VerificationPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Capacity of the in-memory signal buffer between publishers and the engine. When full, the oldest signals are dropped, so publishing never blocks. Defaults to 1024.</summary>
    public int SignalBufferCapacity { get; set; } = 1024;

    /// <summary>Time to wait for a person to approve a step marked with <c>RequireApproval</c>, unless the step sets its own; an unanswered request expires and the run stops. Defaults to 15 minutes.</summary>
    public TimeSpan DefaultApprovalTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How many completed runs the store keeps (see <see cref="IHomeostatControl.GetRecentRunsAsync"/>). Defaults to 50; 0 keeps none.</summary>
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
    /// lasts; if the instance dies, another can take over once it expires. Defaults to 1 minute.
    /// </summary>
    public TimeSpan ClusterLeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Name of the application, included in notifications and audit records. Defaults to the name of the entry assembly.</summary>
    public string ApplicationName { get; set; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "application";

    /// <summary>How often detectors (thread pool, memory, exceptions, HTTP clients, probes, metrics...) take a sample. Defaults to 10 seconds.</summary>
    public TimeSpan DetectionInterval { get; set; } = TimeSpan.FromSeconds(10);
}
