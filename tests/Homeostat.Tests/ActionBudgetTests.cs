using Homeostat.Persistence;

namespace Homeostat.Tests;

public class ActionBudgetTests
{
    [Fact]
    public async Task Refuses_actions_beyond_the_limit_and_recovers_when_the_window_slides()
    {
        var store = new InMemoryHomeostatStore();
        var window = TimeSpan.FromMinutes(10);
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.True(await store.TryConsumeBudgetAsync(t0, 2, window));
        Assert.True(await store.TryConsumeBudgetAsync(t0.AddMinutes(1), 2, window));
        Assert.False(await store.TryConsumeBudgetAsync(t0.AddMinutes(2), 2, window));
        Assert.Equal(2, await store.GetBudgetUsedAsync(t0.AddMinutes(2), window));

        // The first action leaves the window at minute 10.
        Assert.True(await store.TryConsumeBudgetAsync(t0.AddMinutes(10), 2, window));
        Assert.False(await store.TryConsumeBudgetAsync(t0.AddMinutes(10.5), 2, window));
    }
}
