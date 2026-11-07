using Homeostat.Persistence;

namespace Homeostat.Tests;

/// <summary>Shorthands for tests that set the control state directly, decided now (or at the state's own time).</summary>
internal static class StoreTestExtensions
{
    public static ValueTask<bool> SetPauseAsync(this IHomeostatStore store, PauseState? pause)
        => store.TrySetPauseAsync(pause, pause?.At ?? DateTimeOffset.UtcNow);

    public static ValueTask<bool> SetSwitchAsync(this IHomeostatStore store, string name, SwitchState? state)
        => store.TrySetSwitchAsync(name, state, state?.At ?? DateTimeOffset.UtcNow);

    public static ValueTask<bool> TryReleaseSwitchAsync(this IHomeostatStore store, string name, string condition, string? instance)
        => store.TryReleaseSwitchAsync(name, condition, instance, DateTimeOffset.UtcNow);
}
