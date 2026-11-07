using System.Diagnostics.CodeAnalysis;
using Homeostat.Calls;
using Homeostat.EntityFrameworkCore;
using Homeostat.Probes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat;

/// <summary>Entity Framework Core signals for Homeostat.</summary>
public static class HomeostatEntityFrameworkCoreExtensions
{
    /// <summary>
    /// Publishes, at every detection interval, the failure ratio and 95th percentile duration of the commands that
    /// <typeparamref name="TContext"/> runs, as the signal <c>efcore:{context type name}</c>. The interceptor is added
    /// with <c>ConfigureDbContext</c>, so <c>AddDbContext</c>, <c>AddDbContextPool</c> and <c>AddDbContextFactory</c>
    /// stay as they are. Commands that fail because of the command itself (a constraint violation) do not count; see
    /// <see cref="DbContextSignalOptions.IsFailure"/>. Calling it again for the same context updates the settings without
    /// adding a second interceptor.
    /// </summary>
    /// <typeparam name="TContext">The context.</typeparam>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Source, failure classification and thresholds; optional.</param>
    /// <returns>The builder.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddDbContext&lt;ReportsDbContext&gt;(o => o.UseNpgsql(connectionString));
    /// builder.Services.AddHomeostat(h => h
    ///     .AddDbContextSignals&lt;ReportsDbContext&gt;(o => o.UnhealthyLatency = TimeSpan.FromSeconds(3))
    ///     .AddCondition("reports-db-failing", c => c
    ///         .ForSource("efcore:ReportsDbContext")
    ///         .SustainedFor(TimeSpan.FromMinutes(1))
    ///         .Heal(p => p.TurnOnSwitch("reports-read-only", turnOffWhenCleared: true))));
    /// </code>
    /// </example>
    public static HomeostatBuilder AddDbContextSignals<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicProperties)] TContext>(
        this HomeostatBuilder builder, Action<DbContextSignalOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new DbContextSignalOptions();
        configure?.Invoke(options);
        ArgumentNullException.ThrowIfNull(options.IsFailure, nameof(DbContextSignalOptions.IsFailure));

        var source = string.IsNullOrWhiteSpace(options.Source) ? $"efcore:{typeof(TContext).Name}" : options.Source;
        builder.Services.AddCallSignals(source, "efcore", t =>
        {
            t.DegradedFailureRatio = options.DegradedFailureRatio;
            t.UnhealthyFailureRatio = options.UnhealthyFailureRatio;
            t.DegradedLatency = options.DegradedLatency;
            t.UnhealthyLatency = options.UnhealthyLatency;
            t.MinimumCalls = options.MinimumCalls;
        });

        // Calling this twice for the same context updates the settings but must not add a second interceptor,
        // otherwise every command would be counted twice.
        var existing = builder.Services
            .Where(d => d.ServiceType == typeof(DbContextSignalRegistration))
            .Select(d => d.ImplementationInstance)
            .OfType<DbContextSignalRegistration>()
            .FirstOrDefault(r => r.ContextType == typeof(TContext));
        if (existing is not null)
        {
            existing.Source = source;
            existing.IsFailure = options.IsFailure;
            return builder;
        }

        var registration = new DbContextSignalRegistration(typeof(TContext)) { Source = source, IsFailure = options.IsFailure };
        builder.Services.AddSingleton(registration);
        builder.Services.AddKeyedSingleton(typeof(TContext), (sp, _) =>
            new HomeostatDbInterceptor(sp.GetRequiredKeyedService<CallRecorder>(registration.Source), registration.IsFailure));
        builder.Services.ConfigureDbContext<TContext>((sp, o) => o.AddInterceptors(sp.GetRequiredKeyedService<HomeostatDbInterceptor>(typeof(TContext))));
        return builder;
    }

    /// <summary>
    /// Checks at every probe interval that <typeparamref name="TContext"/> can connect to its database
    /// (<c>Database.CanConnectAsync</c>, in a new scope), and publishes the round trip in milliseconds as the signal
    /// <c>efcore:{context type name}:ping</c>; a context that cannot connect makes the signal Unhealthy.
    /// </summary>
    /// <typeparam name="TContext">
    /// The context, registered with <c>AddDbContext</c>, <c>AddDbContextPool</c> or <c>AddDbContextFactory</c> (the
    /// probe then creates a context from the factory and disposes it).
    /// </typeparam>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Source, interval, timeout and latency thresholds; optional.</param>
    /// <returns>The builder.</returns>
    /// <example><code>h.AddDbContextProbe&lt;ReportsDbContext&gt;(p => p.DegradedLatency = TimeSpan.FromMilliseconds(200))</code></example>
    public static HomeostatBuilder AddDbContextProbe<TContext>(this HomeostatBuilder builder, Action<DbContextProbeOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new DbContextProbeOptions();
        configure?.Invoke(options);
        var name = string.IsNullOrWhiteSpace(options.Source) ? $"efcore:{typeof(TContext).Name}:ping" : options.Source;
        return builder.AddLatencyProbe(name, async (sp, ct) =>
        {
            // Probes run in their own scope: the context is a fresh one, never shared with requests.
            var canConnect = sp.GetService<TContext>() is { } context
                ? await context.Database.CanConnectAsync(ct).ConfigureAwait(false)
                : await CanConnectThroughFactoryAsync<TContext>(sp, ct).ConfigureAwait(false);
            if (!canConnect)
            {
                throw new InvalidOperationException($"{typeof(TContext).Name} cannot connect to its database.");
            }
        }, options.CopyTo);
    }

    private static async Task<bool> CanConnectThroughFactoryAsync<TContext>(IServiceProvider services, CancellationToken cancellationToken)
        where TContext : DbContext
    {
        var factory = services.GetService<IDbContextFactory<TContext>>() ?? throw new InvalidOperationException(
            $"AddDbContextProbe<{typeof(TContext).Name}>: register the context with AddDbContext, AddDbContextPool or AddDbContextFactory.");
        var context = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await context.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>The contexts already instrumented, so a second call does not add a second interceptor.</summary>
internal sealed class DbContextSignalRegistration(Type contextType)
{
    public Type ContextType { get; } = contextType;

    public required string Source { get; set; }

    public required Func<Exception, bool> IsFailure { get; set; }
}
