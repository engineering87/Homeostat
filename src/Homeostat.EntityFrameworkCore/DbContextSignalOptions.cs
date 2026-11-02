using System.Data.Common;
using Homeostat.Calls;

namespace Homeostat.EntityFrameworkCore;

/// <summary>Settings of the command signals of one <c>DbContext</c> (see <c>AddDbContextSignals</c>).</summary>
public sealed class DbContextSignalOptions
{
    /// <summary>Signal source. Defaults to <c>"efcore:{context type name}"</c>.</summary>
    public string? Source { get; set; }

    /// <summary>
    /// Decides which failed commands count as failures of the database. Defaults to <see cref="IsDatabaseFailure"/>:
    /// timeouts and transient errors count, errors caused by the command itself (a constraint violation, a syntax
    /// error) do not. Connections that cannot be opened or closed always count; canceled commands and connections never do.
    /// </summary>
    public Func<Exception, bool> IsFailure { get; set; } = IsDatabaseFailure;

    /// <summary>Failure ratio (0 to 1) at or above which the signal is Degraded. Defaults to 0.1.</summary>
    public double DegradedFailureRatio { get; set; } = 0.1;

    /// <summary>Failure ratio (0 to 1) at or above which the signal is Unhealthy. Defaults to 0.5.</summary>
    public double UnhealthyFailureRatio { get; set; } = 0.5;

    /// <summary>95th percentile command duration at or above which the signal is at least Degraded. <c>null</c> (the default) ignores durations.</summary>
    public TimeSpan? DegradedLatency { get; set; }

    /// <summary>95th percentile command duration at or above which the signal is Unhealthy. <c>null</c> (the default) ignores durations.</summary>
    public TimeSpan? UnhealthyLatency { get; set; }

    /// <summary>Commands an interval needs before its failure ratio is judged (see <see cref="CallSignalOptions.MinimumCalls"/>). Defaults to 10.</summary>
    public int MinimumCalls { get; set; } = 10;

    /// <summary>
    /// Whether an exception from a command means that the database is in trouble: a <see cref="TimeoutException"/>, or
    /// a <see cref="DbException"/> that the provider marks as transient (<see cref="DbException.IsTransient"/>), or
    /// one of those as the inner exception.
    /// </summary>
    /// <param name="exception">The exception of a failed command.</param>
    /// <returns><c>true</c> for those exceptions.</returns>
    public static bool IsDatabaseFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or DbException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }
}
