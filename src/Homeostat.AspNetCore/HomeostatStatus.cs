using Homeostat.Conditions;
using Homeostat.Healing;

namespace Homeostat.AspNetCore;

/// <summary>Body of the status endpoint.</summary>
/// <param name="Application">Application name.</param>
/// <param name="Paused">Whether the kill switch is on.</param>
/// <param name="DryRun">Whether actions are only logged.</param>
/// <param name="Budget">Action budget use.</param>
/// <param name="Conditions">Every registered condition.</param>
/// <param name="Switches">Switches currently on.</param>
/// <param name="PendingApprovals">Steps waiting for approval.</param>
/// <param name="RecentRuns">Most recent runs, newest first.</param>
public sealed record HomeostatStatus(
    string Application,
    bool Paused,
    bool DryRun,
    BudgetStatus Budget,
    IReadOnlyList<ConditionStatusInfo> Conditions,
    IReadOnlyCollection<string> Switches,
    IReadOnlyList<PendingApproval> PendingApprovals,
    IReadOnlyList<RunInfo> RecentRuns);

/// <summary>Action budget use.</summary>
/// <param name="Used">Actions in the current window.</param>
/// <param name="Limit">Maximum per window.</param>
/// <param name="Remaining">Actions still allowed.</param>
/// <param name="WindowSeconds">Window length in seconds.</param>
public sealed record BudgetStatus(int Used, int Limit, int Remaining, double WindowSeconds);

/// <summary>State of one condition.</summary>
/// <param name="Name">Condition name.</param>
/// <param name="Status"><c>Inactive</c>, <c>Pending</c> or <c>Active</c>.</param>
/// <param name="PendingSince">Start of the current episode.</param>
/// <param name="ActiveSince">When it became active.</param>
/// <param name="ClearingSince">When recovery started, while waiting for <c>ClearAfter</c>.</param>
/// <param name="SuppressedBy">Active condition suppressing this one.</param>
/// <param name="LastSignalAt">Latest relevant signal time.</param>
/// <param name="LastSeverity">Latest relevant signal severity.</param>
/// <param name="LastMessage">Latest relevant signal message.</param>
public sealed record ConditionStatusInfo(
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
public sealed record RunInfo(string Condition, string Outcome, DateTimeOffset StartedAt, DateTimeOffset CompletedAt, IReadOnlyList<StepInfo> Steps)
{
    internal static RunInfo From(HealingRunResult r) => new(
        r.ConditionName, r.Outcome.ToString(), r.StartedAt, r.CompletedAt,
        r.Steps.Select(s => new StepInfo(s.Name, s.Succeeded, s.Verification?.ToString(), s.Error?.Message)).ToArray());
}

/// <summary>An executed step.</summary>
/// <param name="Name">Step name.</param>
/// <param name="Succeeded">Whether the action completed.</param>
/// <param name="Verification">Verification outcome.</param>
/// <param name="Error">Error message, if the action or verifier failed.</param>
public sealed record StepInfo(string Name, bool Succeeded, string? Verification, string? Error);
