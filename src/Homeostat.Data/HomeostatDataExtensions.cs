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
    /// connections in use (<c>number-of-active-connections</c>) as a share of the maximum pool size. A pool that stays
    /// full makes every new request wait, then fail with "timeout expired prior to obtaining a connection from the
    /// pool", often long before the database itself is in trouble. No reference to SqlClient is needed.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Maximum pool size, source and thresholds; optional.</param>
    /// <remarks>
    /// The counter covers every SqlClient pool of the process: with several connection strings, set
    /// <see cref="PoolSignalOptions.MaxPoolSize"/> to the sum of their pool sizes. Requires Microsoft.Data.SqlClient 3.0
    /// or later.
    /// </remarks>
    /// <example><code>h.AddSqlClientPoolSignal(o => o.MaxPoolSize = 200)</code></example>
    public static HomeostatBuilder AddSqlClientPoolSignal(this HomeostatBuilder builder, Action<SqlClientPoolSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new SqlClientPoolSignalOptions();
        configure?.Invoke(options);
        options.Validate();
        return builder.AddEventCounterSignal("Microsoft.Data.SqlClient.EventSource", "number-of-active-connections", o =>
        {
            o.Source = options.Source;
            o.Transform = active => active / options.MaxPoolSize;
            o.DegradedAbove = options.DegradedAbove;
            o.UnhealthyAbove = options.UnhealthyAbove;
        });
    }

    /// <summary>
    /// Publishes how full an Npgsql connection pool is, from Npgsql's own metrics: the connections in use
    /// (<c>db.client.connection.count</c> with state <c>used</c>, meter <c>Npgsql</c>) as a share of the maximum pool
    /// size. Like <see cref="AddSqlClientPoolSignal"/>, it warns before requests start failing with a pool timeout. No
    /// reference to Npgsql is needed.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Maximum pool size, data source, source and thresholds; optional.</param>
    /// <remarks>Requires Npgsql 10 or later, which publishes the OpenTelemetry connection metrics under these names.</remarks>
    /// <example>
    /// <code>
    /// h.AddNpgsqlPoolSignal(o => { o.MaxPoolSize = 50; o.DataSourceName = "reports"; })
    ///  .AddCondition("reports-pool-full", c => c.ForSource("npgsql:pool").SustainedFor(TimeSpan.FromMinutes(1))
    ///      .Heal(p => p.TurnOnSwitch("defer-heavy-reports")));   // shed load that holds connections
    /// </code>
    /// </example>
    public static HomeostatBuilder AddNpgsqlPoolSignal(this HomeostatBuilder builder, Action<NpgsqlPoolSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new NpgsqlPoolSignalOptions();
        configure?.Invoke(options);
        options.Validate();
        return builder.AddMetricSignal("db.client.connection.count", m =>
        {
            m.FromMeter("Npgsql")
                .Source(options.Source)
                .Where("db.client.connection.state", "used")
                .Statistic(MetricStatistic.Last)
                .Scale(1.0 / options.MaxPoolSize)
                .DegradedAbove(options.DegradedAbove)
                .UnhealthyAbove(options.UnhealthyAbove);
            if (options.DataSourceName is not null)
            {
                m.Where("db.client.connection.pool.name", options.DataSourceName);
            }
        });
    }
}
