using Homeostat.Healing;

namespace Homeostat.Tests;

/// <summary>
/// Synchronous shorthands for the control API in tests: with the in-memory store every call completes at once, so the
/// tests read as before while going through the asynchronous, store-backed implementation.
/// </summary>
internal static class ControlTestExtensions
{
    public static void TurnOn(this IHomeostatSwitches switches, string name) => switches.TurnOnAsync(name).AsTask().GetAwaiter().GetResult();

    public static void TurnOff(this IHomeostatSwitches switches, string name) => switches.TurnOffAsync(name).AsTask().GetAwaiter().GetResult();

    public static void Pause(this IHomeostatControl control) => control.PauseAsync().AsTask().GetAwaiter().GetResult();

    public static void PauseUntil(this IHomeostatControl control, DateTimeOffset until) => control.PauseAsync(until).AsTask().GetAwaiter().GetResult();

    public static void Resume(this IHomeostatControl control) => control.ResumeAsync().AsTask().GetAwaiter().GetResult();

    public static IReadOnlyList<PendingApproval> GetPending(this IHomeostatApprovals approvals) => approvals.GetPendingAsync().AsTask().GetAwaiter().GetResult();

    public static bool Approve(this IHomeostatApprovals approvals, string id, string? by = null) => approvals.ApproveAsync(id, by).AsTask().GetAwaiter().GetResult();

    public static bool Reject(this IHomeostatApprovals approvals, string id, string? by = null) => approvals.RejectAsync(id, by).AsTask().GetAwaiter().GetResult();
}
