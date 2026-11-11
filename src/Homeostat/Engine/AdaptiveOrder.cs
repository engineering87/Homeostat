using Homeostat.Healing;
using Homeostat.Persistence;

namespace Homeostat.Engine;

/// <summary>Orders the steps of an adaptive plan by their measured success.</summary>
internal static class AdaptiveOrder
{
    /// <summary>
    /// Reorders, within each group of steps between approval steps, the steps that have at least
    /// <paramref name="minimumAttempts"/> attempts each, by smoothed success rate (healed + 1) / (attempts + 2), among the
    /// slots they occupy. Steps with fewer attempts and approval steps stay where they are.
    /// </summary>
    public static IReadOnlyList<HealingStep> Order(
        IReadOnlyList<HealingStep> steps, IEnumerable<HealingRunRecord> history, string condition, int minimumAttempts)
    {
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        var healed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var run in history)
        {
            // Records come from the store: a hand-edited file or a custom store may leave fields out. Such a record is
            // no evidence, and must not make every adaptive run fail.
            if (run is null || !string.Equals(run.ConditionName, condition, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var step in run.Steps ?? [])
            {
                if (step?.Name is null)
                {
                    continue;
                }

                attempts[step.Name] = attempts.GetValueOrDefault(step.Name) + 1;
                if (step.Succeeded && step.Verification == VerificationOutcome.Healed)
                {
                    healed[step.Name] = healed.GetValueOrDefault(step.Name) + 1;
                }
            }
        }

        var ordered = new List<HealingStep>(steps.Count);
        var group = new List<(HealingStep Step, int Index)>();

        // Only steps with enough attempts of their own are evidence; the others (new, rarely reached) keep their
        // declared slot, so a step that was never tried is not pushed behind or ahead of anything by a guess.
        void Flush()
        {
            var evaluated = group.Where(g => attempts.GetValueOrDefault(g.Step.Name) >= minimumAttempts).ToList();
            var sorted = new Queue<HealingStep>(evaluated
                .OrderByDescending(g => (healed.GetValueOrDefault(g.Step.Name) + 1.0) / (attempts.GetValueOrDefault(g.Step.Name) + 2.0))
                .ThenBy(g => g.Index)
                .Select(g => g.Step));
            foreach (var (step, _) in group)
            {
                ordered.Add(attempts.GetValueOrDefault(step.Name) >= minimumAttempts ? sorted.Dequeue() : step);
            }

            group.Clear();
        }

        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].RequiresApproval)
            {
                Flush();
                ordered.Add(steps[i]);
            }
            else
            {
                group.Add((steps[i], i));
            }
        }

        Flush();
        return ordered;
    }
}
