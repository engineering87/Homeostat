using System.Data.Common;
using Homeostat.Data;
using Homeostat.Metrics;
using Homeostat.Probes;

namespace Homeostat;

/// <summary>Registers database probes on the Homeostat builder.</summary>
public static class HomeostatDataExtensions
{
    /// <summary>
    /// Periodically opens a connection and runs <see cref="DatabaseProbeOptions.CommandText"/>, publishing the round
    /// trip in milliseconds as the value of a signal from <paramref name="source"/>. A failure publishes
    /// <see cref="ProbeOptions.FailureSeverity"/> without a value, tagged with the exception type and, for a
    /// <see cref="DbException"/>, whether the provider reports it as transient (<c>transient=true</c>).
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="source">Source of the published signals, for example <c>"reports-db"</c>.</param>
    /// <param name="createConnection">
    /// Creates a new, closed connection, which the probe opens and disposes. Do not return a connection owned by
    /// something else, such as the one of an Entity Framework Core <c>DbContext</c>.
    /// </param>
    /// <param name="configure">Changes the query, interval, timeout and thresholds.</param>
    /// <example>
    /// <code>
    /// h.AddDatabaseProbe("reports-db", _ => new SqlConnection(connectionString),
    ///     p => { p.DegradedAbove = 200; p.UnhealthyAbove = 2_000; });
    /// </code>
    /// </example>
    public static HomeostatBuilder AddDatabaseProbe(
        this HomeostatBuilder builder,
        string source,
        Func<IServiceProvider, DbConnection> createConnection,
        Action<DatabaseProbeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(createConnection);

        var options = new DatabaseProbeOptions();
        configure?.Invoke(options);
        options.Origin ??= "database-probe";
        var commandText = options.CommandText;
        var commandTimeout = (int)Math.Clamp(Math.Ceiling(options.Timeout.TotalSeconds), 1, int.MaxValue);

        return builder.AddLatencyProbe(source, async (sp, ct) =>
        {
            var connection = createConnection(sp)
                ?? throw new InvalidOperationException($"The connection factory of database probe '{source}' returned null.");
            await using (connection.ConfigureAwait(false))
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(commandText))
                {
                    var command = connection.CreateCommand();
                    await using (command.ConfigureAwait(false))
                    {
                        command.CommandText = commandText;
                        command.CommandTimeout = commandTimeout;
                        await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    }
                }
            }
        }, options.CopyTo);
    }

    /// <summary>
    /// Publishes how full the <c>Microsoft.Data.SqlClient</c> connection pool is, from its event counters: the
    /// connections in use (<c>number-of-active-connections</c>) as a share of <paramref name="maxPoolSize"/>. A pool
    /// that stays full makes every new request wait, then fail with "timeout expired prior to obtaining a connection
    /// from the pool", often long before the database itself is in trouble. No reference to SqlClient is needed.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="maxPoolSize">The <c>Max Pool Size</c> of the connection string; 100 by default.</param>
    /// <param name="source">Signal source.</param>
    /// <param name="degradedAbove">Share (0 to 1) from which the signal is Degraded.</param>
    /// <param name="unhealthyAbove">Share (0 to 1) from which the signal is Unhealthy.</param>
    /// <remarks>
    /// The counter covers every SqlClient pool of the process: with several connection strings, set
    /// <paramref name="maxPoolSize"/> to the sum of their pool sizes. Requires Microsoft.Data.SqlClient 3.0 or later.
    /// </remarks>
    public static HomeostatBuilder AddSqlClientPoolSignal(
        this HomeostatBuilder builder,
        int maxPoolSize = 100,
        string source = "sqlclient:pool",
        double degradedAbove = 0.8,
        double unhealthyAbove = 0.95)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoolSize, 1);
        return builder.AddEventCounterSignal("Microsoft.Data.SqlClient.EventSource", "number-of-active-connections", o =>
        {
            o.Source = source;
            o.Transform = active => active / maxPoolSize;
            o.DegradedAbove = degradedAbove;
            o.UnhealthyAbove = unhealthyAbove;
        });
    }

    /// <summary>
    /// Publishes how full an Npgsql connection pool is, from Npgsql's own metrics: the connections in use
    /// (<c>db.client.connection.count</c> with state <c>used</c>, meter <c>Npgsql</c>) as a share of
    /// <paramref name="maxPoolSize"/>. Like <see cref="AddSqlClientPoolSignal"/>, it warns before requests start
    /// failing with a pool timeout. No reference to Npgsql is needed.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="maxPoolSize">The <c>Maximum Pool Size</c> of the connection string; 100 by default.</param>
    /// <param name="dataSourceName">
    /// The pool to watch, by the name of its data source (<c>NpgsqlDataSourceBuilder.Name</c>, by default the
    /// connection string without the password). <c>null</c> adds up every Npgsql pool of the process: then set
    /// <paramref name="maxPoolSize"/> to the sum of their sizes.
    /// </param>
    /// <param name="source">Signal source.</param>
    /// <param name="degradedAbove">Share (0 to 1) from which the signal is Degraded.</param>
    /// <param name="unhealthyAbove">Share (0 to 1) from which the signal is Unhealthy.</param>
    /// <remarks>Requires Npgsql 10 or later, which publishes the OpenTelemetry connection metrics under these names.</remarks>
    /// <example>
    /// <code>
    /// h.AddNpgsqlPoolSignal(maxPoolSize: 50, dataSourceName: "reports")
    ///  .AddCondition("reports-pool-full", c => c.ForSource("npgsql:pool").SustainedFor(TimeSpan.FromMinutes(1))
    ///      .Heal(p => p.TurnOnSwitch("defer-heavy-reports", turnOffWhenCleared: true)));   // shed load that holds connections
    /// </code>
    /// </example>
    public static HomeostatBuilder AddNpgsqlPoolSignal(
        this HomeostatBuilder builder,
        int maxPoolSize = 100,
        string? dataSourceName = null,
        string source = "npgsql:pool",
        double degradedAbove = 0.8,
        double unhealthyAbove = 0.95)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoolSize, 1);
        return builder.AddMetricSignal("db.client.connection.count", m =>
        {
            m.FromMeter("Npgsql")
                .Source(source)
                .Where("db.client.connection.state", "used")
                .Statistic(MetricStatistic.Last)
                .Scale(1.0 / maxPoolSize)
                .DegradedAbove(degradedAbove)
                .UnhealthyAbove(unhealthyAbove);
            if (dataSourceName is not null)
            {
                m.Where("db.client.connection.pool.name", dataSourceName);
            }
        });
    }
}
