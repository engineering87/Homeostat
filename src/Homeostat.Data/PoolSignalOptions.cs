namespace Homeostat.Data;

/// <summary>Settings of a connection pool signal (<c>AddSqlClientPoolSignal</c>, <c>AddNpgsqlPoolSignal</c>).</summary>
public class PoolSignalOptions
{
    private protected PoolSignalOptions(string source) => Source = source;

    /// <summary>The maximum pool size of the connection string; 100 by default, the providers' default.</summary>
    public int MaxPoolSize { get; set; } = 100;

    /// <summary>Signal source.</summary>
    public string Source { get; set; }

    /// <summary>Share of the pool in use (0 to 1) from which the signal is Degraded. Defaults to 0.8.</summary>
    public double DegradedAbove { get; set; } = 0.8;

    /// <summary>Share of the pool in use (0 to 1) from which the signal is Unhealthy. Defaults to 0.95.</summary>
    public double UnhealthyAbove { get; set; } = 0.95;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPoolSize, 1, nameof(MaxPoolSize));
        ArgumentException.ThrowIfNullOrWhiteSpace(Source, nameof(Source));
    }
}

/// <summary>Settings of <c>AddSqlClientPoolSignal</c>. Source <c>sqlclient:pool</c> by default.</summary>
public sealed class SqlClientPoolSignalOptions() : PoolSignalOptions("sqlclient:pool");

/// <summary>Settings of <c>AddNpgsqlPoolSignal</c>. Source <c>npgsql:pool</c> by default.</summary>
public sealed class NpgsqlPoolSignalOptions() : PoolSignalOptions("npgsql:pool")
{
    /// <summary>
    /// The pool to watch, by the name of its data source (<c>NpgsqlDataSourceBuilder.Name</c>, by default the connection
    /// string without the password). <c>null</c> (the default) adds up every Npgsql pool of the process: then set
    /// <see cref="PoolSignalOptions.MaxPoolSize"/> to the sum of their sizes.
    /// </summary>
    public string? DataSourceName { get; set; }
}
