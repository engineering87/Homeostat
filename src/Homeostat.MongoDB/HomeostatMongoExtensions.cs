using Homeostat.Detectors;
using Homeostat.MongoDB;
using Homeostat.Probes;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Servers;

namespace Homeostat;

/// <summary>MongoDB signals: ping round trip, primary availability, connection pool and replication lag.</summary>
public static class HomeostatMongoExtensions
{
    /// <summary>
    /// Publishes the health of a MongoDB deployment:
    /// <list type="bullet">
    /// <item><description><c>mongodb:{name}:ping</c>: round trip of a <c>ping</c> command, in milliseconds;</description></item>
    /// <item><description><c>mongodb:{name}:primary</c>: whether a writable server is connected (Unhealthy during an
    /// election or a network split), from the driver events;</description></item>
    /// <item><description><c>mongodb:{name}:pool</c>: connection checkouts that timed out in the interval (the pool is
    /// exhausted), from the driver events;</description></item>
    /// <item><description><c>mongodb:{name}:replication-lag</c>, with <see cref="MongoSignalOptions.ReplicationLag"/>:
    /// seconds the furthest secondary is behind.</description></item>
    /// </list>
    /// The event-based signals need the client to be created with <see cref="AddHomeostatSignals"/> on its settings.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="name">Name of the deployment in the signal sources, <c>mongodb:{name}:...</c>. Defaults to <c>"default"</c>.</param>
    /// <param name="configure">Thresholds, interval, replication lag and client.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddSingleton&lt;IMongoClient&gt;(sp =>
    /// {
    ///     var settings = MongoClientSettings.FromConnectionString(builder.Configuration.GetConnectionString("archive"));
    ///     settings.AddHomeostatSignals(sp, "archive");
    ///     return new MongoClient(settings);
    /// });
    /// builder.Services.AddHomeostat(h => h.AddMongoSignals("archive", o => o.Ping.DegradedLatency = TimeSpan.FromMilliseconds(100)));
    /// </code>
    /// </example>
    public static HomeostatBuilder AddMongoSignals(this HomeostatBuilder builder, string name = "default", Action<MongoSignalOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var options = new MongoSignalOptions();
        configure?.Invoke(options);
        if (options.Interval <= TimeSpan.Zero || options.PoolTimeoutsUnhealthyAbove < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configure), "The interval must be positive and PoolTimeoutsUnhealthyAbove not negative.");
        }

        Func<IServiceProvider, IMongoClient> client = options.Client ?? (sp => sp.GetRequiredService<IMongoClient>());
        void Settings(ProbeOptions from, ProbeOptions to)
        {
            from.CopyTo(to);
            to.Interval ??= options.Interval;
            to.Origin ??= "mongodb";
        }

        var prefix = MongoSignals.Prefix(name, options);
        builder.AddLatencyProbe($"{prefix}:ping",
            (sp, ct) => client(sp).GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct),
            p => Settings(options.Ping, p));

        if (options.ReplicationLag)
        {
            builder.AddProbe($"{prefix}:replication-lag", async (sp, ct) =>
            {
                var status = await client(sp).GetDatabase("admin")
                    .RunCommandAsync<BsonDocument>(new BsonDocument("replSetGetStatus", 1), cancellationToken: ct).ConfigureAwait(false);
                return ReplicationLag.Seconds(Members(status), options.LagExcludedMembers);
            }, p => Settings(options.Lag, p));
        }

        builder.Services.TryAddSingleton<MongoClientEvents>();
        if (builder.Services.Any(d => d.ImplementationInstance is MongoRegistration r && r.Name == name))
        {
            throw new InvalidOperationException($"MongoDB signals named '{name}' are already registered: give each deployment its own name.");
        }

        builder.Services.AddSingleton(new MongoRegistration(name, options));
        builder.Services.AddHomeostatDetector<MongoSignalDetector>();
        return builder;
    }

    /// <summary>
    /// Subscribes the client created with these settings to the driver events that the MongoDB signals need (cluster
    /// changes and connection checkout failures), keeping any <c>ClusterConfigurator</c> already set. Call it before
    /// creating the client; <c>AddMongoSignals</c> with the same name publishes the signals.
    /// </summary>
    /// <remarks>
    /// Register the client as a singleton, as MongoDB recommends: the same settings get the same configurator, so the
    /// driver still shares one cluster between them, but a client per request or per tenant with different settings
    /// opens clusters that the driver keeps until the process ends.
    /// </remarks>
    /// <param name="settings">The client settings.</param>
    /// <param name="services">The application's services.</param>
    /// <param name="name">Name used in <c>AddMongoSignals</c>. Defaults to <c>"default"</c>.</param>
    public static MongoClientSettings AddHomeostatSignals(this MongoClientSettings settings, IServiceProvider services, string name = "default")
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var events = services.GetService<MongoClientEvents>();
        var registration = services.GetServices<MongoRegistration>().LastOrDefault(r => r.Name == name);
        if (events is null || registration is null)
        {
            throw new InvalidOperationException($"Call AddMongoSignals(\"{name}\") in AddHomeostat to publish the MongoDB signals.");
        }

        var publisher = services.GetRequiredService<ISignalPublisher>();
        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var state = events.For(name);
        state.Subscribed = true;

        var previous = settings.ClusterConfigurator;
        settings.ClusterConfigurator = events.Configurator(name, previous, () => cluster =>
        {
            previous?.Invoke(cluster);
            cluster.Subscribe<ConnectionPoolCheckingOutConnectionFailedEvent>(e =>
                state.CheckoutFailed(e.Reason == ConnectionCheckOutFailedReason.Timeout));
            cluster.Subscribe<ClusterDescriptionChangedEvent>(e =>
            {
                var servers = e.NewDescription.Servers;

                // The first descriptions come before any heartbeat (and, with mongodb+srv, before any server is known):
                // "not connected yet" is not "no primary".
                if (!servers.Any(s => s.State == ServerState.Connected || s.HeartbeatException is not null || s.LastHeartbeatTimestamp is not null))
                {
                    return;
                }

                var writable = servers.Any(s => s.State == ServerState.Connected && s.Type.IsWritable());
                var connected = servers.Count(s => s.State == ServerState.Connected);

                // A lost primary is published at once, not at the next interval.
                if (state.ClusterChanged(e.ClusterId.Value, writable, connected))
                {
                    MongoSignals.PublishPrimary(publisher, registration.Prefix, state, time.GetUtcNow());
                }
            });
        });
        return settings;
    }

    private static IEnumerable<ReplicaMember> Members(BsonDocument status)
    {
        if (!status.TryGetValue("members", out var members) || !members.IsBsonArray)
        {
            yield break;
        }

        foreach (var member in members.AsBsonArray)
        {
            if (!member.IsBsonDocument)
            {
                continue;
            }

            var document = member.AsBsonDocument;
            var state = document.TryGetValue("stateStr", out var s) && s.IsString ? s.AsString : string.Empty;
            var memberName = document.TryGetValue("name", out var n) && n.IsString ? n.AsString : string.Empty;
            DateTime? optime = document.TryGetValue("optimeDate", out var o) && o.IsValidDateTime ? o.ToUniversalTime() : null;
            yield return new ReplicaMember(memberName, state, optime);
        }
    }
}
