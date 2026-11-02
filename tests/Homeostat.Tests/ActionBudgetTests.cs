using Homeostat.Guardrails;
using Microsoft.Extensions.Options;

namespace Homeostat.Tests;

public class ActionBudgetTests
{
    [Fact]
    public void Refuses_actions_beyond_the_limit_and_recovers_when_the_window_slides()
    {
        var budget = new ActionBudget(Options.Create(new HomeostatOptions
        {
            MaxActionsPerWindow = 2,
            ActionBudgetWindow = TimeSpan.FromMinutes(10),
        }));
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(budget.TryConsume(t0));
        Assert.True(budget.TryConsume(t0.AddMinutes(1)));
        Assert.False(budget.TryConsume(t0.AddMinutes(2)));

        // The first action leaves the window at minute 10.
        Assert.True(budget.TryConsume(t0.AddMinutes(10)));
        Assert.False(budget.TryConsume(t0.AddMinutes(10.5)));
    }
}
