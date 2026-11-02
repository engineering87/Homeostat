using Homeostat.Consumers;
using Homeostat.Healing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Homeostat;

/// <summary>Pausing and resuming message consumers.</summary>
public static class HomeostatConsumerExtensions
{
    /// <summary>
    /// Declares a consumer that Homeostat can pause and resume, created from the services when the host starts. For
    /// consumers created later, register them with <see cref="IHomeostatConsumers.Register"/>.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="consumer">Returns the consumer, for example an adapter around a registered processor.</param>
    /// <example>
    /// <code>
    /// h.AddPausableConsumer(sp => new PausableServiceBusProcessor("ingest", sp.GetRequiredService&lt;ServiceBusProcessor&gt;()))
    ///  .AddCondition("archive-down", c => c.ForSource("archive-db").Heal(p => p.PauseConsumer("ingest")));
    /// </code>
    /// </example>
    public static HomeostatBuilder AddPausableConsumer(this HomeostatBuilder builder, Func<IServiceProvider, IPausableConsumer> consumer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(consumer);
        AddConsumerServices(builder.Services);
        builder.Services.AddSingleton(new ConsumerRegistration(consumer));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ConsumerRegistrar>());
        return builder;
    }

    /// <summary>
    /// Registers a <see cref="PauseGate"/> named <paramref name="name"/> as a keyed singleton and declares it as a
    /// pausable consumer: a loop that pulls work (a receive loop, a polling worker) resolves it with
    /// <c>GetRequiredKeyedService&lt;PauseGate&gt;(name)</c> and awaits <see cref="PauseGate.WaitWhilePausedAsync"/>
    /// before taking the next item. The gate outlives the loop, so a restarted worker keeps the paused state.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="name">Consumer name, used by <see cref="PauseConsumer"/>.</param>
    /// <example>
    /// <code>
    /// h.AddPauseGate("telemetry-ingest")
    ///  .AddCondition("archive-down", c => c.ForSource("archive-db").Heal(p => p.PauseConsumer("telemetry-ingest")));
    ///
    /// // In the worker, resolved from the services:
    /// var gate = services.GetRequiredKeyedService&lt;PauseGate&gt;("telemetry-ingest");
    /// while (!stoppingToken.IsCancellationRequested)
    /// {
    ///     await gate.WaitWhilePausedAsync(stoppingToken);
    ///     var message = await receiver.ReceiveAsync(stoppingToken);
    ///     // ...
    /// }
    /// </code>
    /// </example>
    public static HomeostatBuilder AddPauseGate(this HomeostatBuilder builder, string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        builder.Services.AddKeyedSingleton(name, (_, _) => new PauseGate(name));
        return builder.AddPausableConsumer(sp => sp.GetRequiredKeyedService<PauseGate>(name));
    }

    /// <summary>
    /// Appends a step that pauses the consumers named <paramref name="consumer"/> (it turns on the switch
    /// <c>consumer:{name}:paused</c>): they stop taking messages while a dependency they write to is down, instead of
    /// failing every message into the dead-letter queue.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="consumer">Consumer name.</param>
    /// <param name="resumeWhenCleared">Resumes them when the condition clears (default). Otherwise a person resumes them by turning the switch off.</param>
    public static HealingPlanBuilder PauseConsumer(this HealingPlanBuilder plan, string consumer, bool resumeWhenCleared = true)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.TurnOnSwitch(ConsumerSwitches.Paused(consumer), resumeWhenCleared);
    }

    internal static void AddConsumerServices(IServiceCollection services)
    {
        services.TryAddSingleton<HomeostatConsumers>();
        services.TryAddSingleton<IHomeostatConsumers>(sp => sp.GetRequiredService<HomeostatConsumers>());
    }
}
