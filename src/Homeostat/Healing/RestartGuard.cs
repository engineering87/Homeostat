using Homeostat.Healing.Actions;
using Homeostat.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Healing;

/// <summary>Limits of actions that restart the application (its process, its pod).</summary>
/// <param name="MinimumUptime">Least time since this instance started: a process that fails right after starting is not restarted again.</param>
/// <param name="MaxRestarts">Most restarts of the application within <paramref name="Window"/>, counted in the store, across instances and processes.</param>
/// <param name="Window">Window of <paramref name="MaxRestarts"/>: a fixed window that starts at the first restart counted in it.</param>
public sealed record RestartLimits(TimeSpan MinimumUptime, int MaxRestarts, TimeSpan Window)
{
    /// <summary>10 minutes of uptime, at most 3 restarts per hour.</summary>
    public static RestartLimits Default { get; } = new(TimeSpan.FromMinutes(10), 3, TimeSpan.FromHours(1));

    internal void Validate()
    {
        if (MinimumUptime < TimeSpan.Zero || MaxRestarts < 1 || Window <= TimeSpan.Zero)
        {
            throw new ArgumentException("RestartLimits: MinimumUptime cannot be negative, MaxRestarts must be at least 1 and Window positive.");
        }
    }
}

/// <summary>
/// The checks every action that restarts the application performs first. A restart ends the healing run (the run is
/// recorded as canceled by the shutdown), and a new process starts with a fresh view: without these limits a plan
/// could restart the application in a loop.
/// </summary>
public static class RestartGuard
{
    /// <summary>
    /// Throws when this instance has been up for less than <see cref="RestartLimits.MinimumUptime"/>, or when the
    /// application has already restarted <see cref="RestartLimits.MaxRestarts"/> times within the window; otherwise
    /// counts this restart in the store. With the in-memory store the count does not survive the restart: use
    /// <c>UseFileStore</c> or <c>UseRedis</c>.
    /// </summary>
    /// <param name="context">The healing context of the restarting step.</param>
    /// <param name="limits">The limits to enforce.</param>
    /// <param name="cancellationToken">Canceled at the action timeout.</param>
    /// <exception cref="InvalidOperationException">The restart is not allowed now.</exception>
    public static async Task EnsureAllowedAsync(HealingContext context, RestartLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(limits);

        var services = context.Services;
        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var clock = services.GetRequiredService<ApplicationClock>();
        var now = time.GetUtcNow();

        var uptime = now - clock.StartedAt;
        if (uptime < limits.MinimumUptime)
        {
            throw new InvalidOperationException(
                $"This instance started {uptime.TotalMinutes:0.#} minutes ago, less than the minimum uptime of {limits.MinimumUptime.TotalMinutes:0.#} minutes: " +
                "restarting now could loop. Escalate instead.");
        }

        // Counted per application and fixed window of the configured length, shared by the instances through the store
        // (ConsecutiveFailures is the count). Each attempt is counted first, atomically, and allowed only if the attempts
        // of this window so far plus the restarts allowed in the previous one stay within the limit: any period of one
        // window length lies within two consecutive windows, so it never holds more than MaxRestarts restarts, and when
        // every replica of a fleet tries at the same moment at most MaxRestarts of them restart. No counter is ever reset,
        // so no reset can race with a count. Refused attempts count against their own window only (the next window looks
        // at the restarts allowed), so a condition that keeps trying cannot block restarts for good.
        var store = services.GetRequiredService<IHomeostatStore>();
        if (store is InMemoryHomeostatStore)
        {
            services.GetService<ILoggerFactory>()?.CreateLogger("Homeostat.Restarts").LogWarning(
                "Restart limits are counted in the in-memory store, which a restart empties: the limit is per process only. "
                + "Use UseFileStore or UseRedis to count restarts across restarts and instances.");
        }

        var application = services.GetRequiredService<IOptions<HomeostatOptions>>().Value.ApplicationName;
        var window = now.UtcTicks / limits.Window.Ticks;
        string Key(string what, long index) => $"restarts/{application}/{what}/{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var increment = new PlanStateUpdate { Failures = FailureCountChange.Increment };

        var attempts = await store.UpdatePlanStateAsync(Key("attempts", window), increment, cancellationToken).ConfigureAwait(false);
        var allowedBefore = await store.GetPlanStateAsync(Key("allowed", window - 1), cancellationToken).ConfigureAwait(false);
        if (attempts.ConsecutiveFailures + allowedBefore.ConsecutiveFailures <= limits.MaxRestarts)
        {
            await store.UpdatePlanStateAsync(Key("allowed", window), increment, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(
                $"The application already restarted {limits.MaxRestarts} times in the last {limits.Window.TotalMinutes:0.#} minutes " +
                $"(limit {limits.MaxRestarts}): restarting again would not help. A person should look at it.");
        }
    }
}
