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
    /// Whether an exception from a command means that the database is in trouble: a <see cref="TimeoutException"/>, a
    /// <see cref="DbException"/> that the provider marks as transient (<see cref="DbException.IsTransient"/>), or one of
    /// those as the inner exception. SQL Server's client does not set <see cref="DbException.IsTransient"/>, so its
    /// timeouts (error -2) and the errors that SQL Server documents as transient (deadlock victim, throttling, failover:
    /// the list EF Core's retrying strategy uses) are recognized by their error number.
    /// </summary>
    /// <param name="exception">The exception of a failed command.</param>
    /// <returns><c>true</c> for those exceptions.</returns>
    public static bool IsDatabaseFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException or DbException { IsTransient: true } || IsSqlServerTransient(current))
            {
                return true;
            }
        }

        return false;
    }

    // Error numbers that EF Core's SqlServerTransientExceptionDetector retries, and -2, the client's timeout.
    private static readonly HashSet<int> SqlServerTransientErrors =
    [
        -2, 20, 64, 121, 233, 1205, 4060, 4221, 10053, 10054, 10060, 10928, 10929, 10936, 12015,
        40197, 40501, 40613, 41301, 41302, 41305, 41325, 41839, 49918, 49919, 49920,
    ];

    // Without a reference to the client: SqlException records the number of its first error in Data["HelpLink.EvtID"],
    // and a client-side timeout wraps a Win32Exception with WAIT_TIMEOUT (258).
    private static bool IsSqlServerTransient(Exception exception)
    {
        if (exception is not DbException { } db || db.GetType().FullName is not ("Microsoft.Data.SqlClient.SqlException" or "System.Data.SqlClient.SqlException"))
        {
            return false;
        }

        if (db.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 258 })
        {
            return true;
        }

        return db.Data["HelpLink.EvtID"] is string number
            && int.TryParse(number, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var code)
            && SqlServerTransientErrors.Contains(code);
    }
}
