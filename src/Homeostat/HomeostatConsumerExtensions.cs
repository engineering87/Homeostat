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
