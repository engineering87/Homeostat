using Homeostat.Healing;

namespace Homeostat.Persistence;

/// <summary>A completed healing run as stored in the history: serializable, without exception objects.</summary>
public sealed record HealingRunRecord
{
    /// <summary>Condition healed.</summary>
    public required string ConditionName { get; init; }

    /// <summary>How the run ended.</summary>
    public required HealingRunOutcome Outcome { get; init; }

    /// <summary>When the run started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the run ended.</summary>
    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Executed steps, in order.</summary>
    public IReadOnlyList<HealingStepRecord> Steps { get; init; } = [];

    /// <summary>Instance that ran it (<see cref="HomeostatOptions.InstanceId"/>).</summary>
    public string Instance { get; init; } = string.Empty;

    /// <summary>
    /// Identifier of the run (<see cref="HealingRunResult.RunId"/>), the one in <see cref="Notifications.HomeostatEvent.RunId"/> and
    /// in the audit log; <c>null</c> for runs stored before it was recorded.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>Creates a record from a run result.</summary>
    /// <param name="run">The run.</param>
    /// <param name="instance">Instance that ran it.</param>
    public static HealingRunRecord From(HealingRunResult run, string instance)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new HealingRunRecord
        {
            ConditionName = run.ConditionName,
            Outcome = run.Outcome,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            Steps = run.Steps.Select(s => new HealingStepRecord
            {
                Name = s.Name,
                Succeeded = s.Succeeded,
                Verification = s.Verification,
                Error = s.Error?.Message,
                UndoSucceeded = s.UndoSucceeded,
            }).ToArray(),
            Instance = instance,
            RunId = run.RunId,
        };
    }
}

/// <summary>An executed step as stored in the history.</summary>
public sealed record HealingStepRecord
{
    /// <summary>Step name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the action completed.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Verification outcome, if the action completed.</summary>
    public VerificationOutcome? Verification { get; init; }

    /// <summary>Error message of the action or verifier, if any.</summary>
    public string? Error { get; init; }

    /// <summary>Whether the compensating action ran successfully; <c>null</c> when it did not run.</summary>
    public bool? UndoSucceeded { get; init; }
}
