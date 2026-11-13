using System.Text.Json.Serialization;
using Homeostat.Conditions;
using Homeostat.Guardrails;
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
/// <param name="Groups">
/// The healing groups known on this instance (<see cref="IHomeostatControl.GetGroups"/>), ordered by name; <c>null</c>,
/// and left out of the JSON, when there are none, so an application without groups sees the same answer as before.
/// </param>
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
    string? StoreError = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<GroupStatusInfo>? Groups = null);

/// <summary>Action budget use.</summary>
/// <param name="Used">Actions in the current window.</param>
/// <param name="Limit">Maximum per window.</param>
/// <param name="Remaining">Actions still allowed.</param>
/// <param name="WindowSeconds">Window length in seconds.</param>
internal sealed record BudgetStatus(int Used, int Limit, int Remaining, double WindowSeconds)
{
    internal static BudgetStatus From(ActionBudgetStatus budget) => new(budget.Used, budget.Limit, budget.Remaining, budget.Window.TotalSeconds);
}

/// <summary>State of one healing group on the instance that answered.</summary>
/// <param name="Name">Group name.</param>
/// <param name="Conditions">The conditions registered in the group on this instance.</param>
/// <param name="Paused">
/// Whether the group itself is paused (by a person or one of its maintenance windows); the global pause, which pauses
/// every group, is the status's own <c>paused</c>.
/// </param>
/// <param name="PauseReason">Why the group itself is paused, or <c>null</c>.</param>
/// <param name="PausedBy">Who paused the group, while its pause applies, or <c>null</c>.</param>
/// <param name="PausedUntil">When the group's pause ends by itself, or <c>null</c> (until resumed, or not paused).</param>
/// <param name="MaxConcurrentHealings">The group's concurrency limit, or <c>null</c>.</param>
/// <param name="MaxActionsPerWindow">The group's own action budget, or <c>null</c> without one (shown even when the store cannot be read).</param>
/// <param name="Budget">The group's action budget use, or <c>null</c> when it has none or the store cannot be read.</param>
internal sealed record GroupStatusInfo(
    string Name,
    IReadOnlyList<string> Conditions,
    bool Paused,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PauseReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PausedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? PausedUntil,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxConcurrentHealings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxActionsPerWindow,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BudgetStatus? Budget)
{
    internal static GroupStatusInfo From(HealingGroupStatus g, ActionBudgetStatus? budget, DateTimeOffset now)
    {
        // A timed pause stays in the store after its end until the group is resumed: shown only while it applies. Not
        // IsPaused, which a maintenance window of the group also sets while the stored pause ended long ago.
        var pause = g.Pause is { } stored && (stored.Until is not { } until || now < until) ? stored : null;
        return new(
            g.Name, g.Conditions, g.IsPaused, g.PauseReason, pause?.By, pause?.Until, g.MaxConcurrentHealings, g.MaxActionsPerWindow,
            budget is null ? null : BudgetStatus.From(budget));
    }
}

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
/// <param name="Group">The condition's healing group, or <c>null</c> (left out of the JSON).</param>
internal sealed record ConditionStatusInfo(
    string Name,
    string Status,
    DateTimeOffset? PendingSince,
    DateTimeOffset? ActiveSince,
    DateTimeOffset? ClearingSince,
    string? SuppressedBy,
    DateTimeOffset? LastSignalAt,
    string? LastSeverity,
    string? LastMessage,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Group = null)
{
    internal static ConditionStatusInfo From(ConditionSnapshot c) => new(
        c.Name, c.Status.ToString(), c.PendingSince, c.ActiveSince, c.ClearingSince, c.SuppressedBy,
        c.LastSignalAt, c.LastSignal?.Severity.ToString(), c.LastSignal?.Message, c.Group);
}

/// <summary>A completed run.</summary>
/// <param name="Condition">Condition healed.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="StartedAt">Start time.</param>
/// <param name="CompletedAt">End time.</param>
/// <param name="Steps">Executed steps.</param>
/// <param name="Instance">Instance that ran it.</param>
/// <param name="Group">The healing group of the condition when the run started, or <c>null</c> (left out of the JSON).</param>
internal sealed record RunInfo(
    string Condition,
    string Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<StepInfo> Steps,
    string Instance,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Group = null)
{
    internal static RunInfo From(HealingRunRecord r) => new(
        r.ConditionName, r.Outcome.ToString(), r.StartedAt, r.CompletedAt,
        // A record from a hand-edited file or a custom store may lack its steps: shown as none, never a failed answer.
        (r.Steps ?? []).Where(s => s is not null).Select(s => new StepInfo(s.Name ?? string.Empty, s.Succeeded, s.Verification?.ToString(), s.Error, s.UndoSucceeded)).ToArray(),
        r.Instance,
        r.Group);
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
