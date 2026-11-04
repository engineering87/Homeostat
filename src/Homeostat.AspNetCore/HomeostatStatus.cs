using Homeostat.Conditions;
using Homeostat.Healing;
using Homeostat.Persistence;

namespace Homeostat.AspNetCore;

/// <summary>Body of the status endpoint.</summary>
/// <param name="Application">Application name.</param>
/// <param name="Instance">Instance that answered (<see cref="HomeostatOptions.InstanceId"/>).</param>
/// <param name="Paused">Whether Homeostat is paused (kill switch, timed pause or maintenance window).</param>
/// <param name="PauseReason">Why it is paused, or <c>null</c>.</param>
/// <param name="DryRun">Whether actions are only logged.</param>
/// <param name="Budget">Action budget use; <c>null</c> when the store is unavailable.</param>
/// <param name="Conditions">Every registered condition.</param>
/// <param name="Switches">Switches currently on.</param>
/// <param name="PendingApprovals">Steps waiting for approval.</param>
/// <param name="RecentRuns">Most recent runs, newest first (empty when the store is unavailable).</param>
/// <param name="StoreError">Why the store could not be read, or <c>null</c>; budget and runs are then missing.</param>
internal sealed record HomeostatStatus(
    string Application,
    string Instance,
    bool Paused,
    string? PauseReason,
    bool DryRun,
    BudgetStatus? Budget,
    IReadOnlyList<ConditionStatusInfo> Conditions,
    IReadOnlyCollection<string> Switches,
    IReadOnlyList<PendingApproval> PendingApprovals,
    IReadOnlyList<RunInfo> RecentRuns,
    string? StoreError = null);

/// <summary>Action budget use.</summary>
/// <param name="Used">Actions in the current window.</param>
/// <param name="Limit">Maximum per window.</param>
/// <param name="Remaining">Actions still allowed.</param>
/// <param name="WindowSeconds">Window length in seconds.</param>
internal sealed record BudgetStatus(int Used, int Limit, int Remaining, double WindowSeconds);

/// <summary>State of one condition.</summary>
/// <param name="Name">Condition name.</param>
/// <param name="Status"><c>Inactive</c>, <c>Pending</c> or <c>Active</c>.</param>
/// <param name="PendingSince">Start of the current episode.</param>
/// <param name="ActiveSince">When it became active.</param>
/// <param name="ClearingSince">When recovery started, while waiting for <c>ClearAfter</c>.</param>
/// <param name="SuppressedBy">Pending or active condition suppressing this one, if any.</param>
/// <param name="LastSignalAt">Latest relevant signal time.</param>
/// <param name="LastSeverity">Latest relevant signal severity.</param>
/// <param name="LastMessage">Latest relevant signal message.</param>
internal sealed record ConditionStatusInfo(
    string Name,
    string Status,
    DateTimeOffset? PendingSince,
    DateTimeOffset? ActiveSince,
    DateTimeOffset? ClearingSince,
    string? SuppressedBy,
    DateTimeOffset? LastSignalAt,
    string? LastSeverity,
    string? LastMessage)
{
    internal static ConditionStatusInfo From(ConditionSnapshot c) => new(
        c.Name, c.Status.ToString(), c.PendingSince, c.ActiveSince, c.ClearingSince, c.SuppressedBy,
        c.LastSignalAt, c.LastSignal?.Severity.ToString(), c.LastSignal?.Message);
}

/// <summary>A completed run.</summary>
/// <param name="Condition">Condition healed.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="StartedAt">Start time.</param>
/// <param name="CompletedAt">End time.</param>
/// <param name="Steps">Executed steps.</param>
/// <param name="Instance">Instance that ran it.</param>
internal sealed record RunInfo(string Condition, string Outcome, DateTimeOffset StartedAt, DateTimeOffset CompletedAt, IReadOnlyList<StepInfo> Steps, string Instance)
{
    internal static RunInfo From(HealingRunRecord r) => new(
        r.ConditionName, r.Outcome.ToString(), r.StartedAt, r.CompletedAt,
        r.Steps.Select(s => new StepInfo(s.Name, s.Succeeded, s.Verification?.ToString(), s.Error, s.UndoSucceeded)).ToArray(),
        r.Instance);
}

/// <summary>An executed step.</summary>
/// <param name="Name">Step name.</param>
/// <param name="Succeeded">Whether the action completed.</param>
/// <param name="Verification">Verification outcome.</param>
/// <param name="Error">Error message, if the action or verifier failed.</param>
/// <param name="UndoSucceeded">Whether the compensating action succeeded; <c>null</c> if it did not run.</param>
internal sealed record StepInfo(string Name, bool Succeeded, string? Verification, string? Error, bool? UndoSucceeded);

/// <summary>A condition becoming active or clearing.</summary>
/// <param name="Condition">Condition name.</param>
/// <param name="Kind"><c>Activated</c> or <c>Cleared</c>.</param>
/// <param name="At">When it happened.</param>
/// <param name="Instance">Instance where it happened.</param>
/// <param name="Source">Source of the signal that caused it.</param>
/// <param name="Severity">Severity of that signal.</param>
/// <param name="Value">Value of that signal, if any.</param>
/// <param name="Message">Message of that signal, if any.</param>
internal sealed record ConditionEventInfo(
    string Condition, string Kind, DateTimeOffset At, string Instance, string? Source, string? Severity, double? Value, string? Message)
{
    internal static ConditionEventInfo From(ConditionEventRecord e) => new(
        e.ConditionName, e.Kind.ToString(), e.At, e.Instance, e.Source, e.Severity?.ToString(), e.Value, e.Message);
}
