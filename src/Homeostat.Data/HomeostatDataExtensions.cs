using System.Data.Common;
using Homeostat.Data;
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
    /// <param name="source">Source of the published signals, for example <c>"catalog-db"</c>.</param>
    /// <param name="createConnection">
    /// Creates a new, closed connection, which the probe opens and disposes. Do not return a connection owned by
    /// something else, such as the one of an Entity Framework Core <c>DbContext</c>.
    /// </param>
    /// <param name="configure">Changes the query, interval, timeout and thresholds.</param>
    /// <example>
    /// <code>
    /// h.AddDatabaseProbe("catalog-db", _ => new SqlConnection(connectionString),
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
}
