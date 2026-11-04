using System.Diagnostics.CodeAnalysis;
using Homeostat.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Homeostat;

/// <summary>Registers <see cref="IHomeostatEventSink"/> implementations.</summary>
public static class HomeostatEventSinkExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TSink"/> once, as a singleton, and delivers it the events of its
    /// <see cref="IHomeostatEventSink.Events"/> in the background. Registering the same type again has no effect.
    /// </summary>
    /// <typeparam name="TSink">The sink type.</typeparam>
    /// <param name="builder">The Homeostat builder.</param>
    public static HomeostatBuilder AddEventSink<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSink>(this HomeostatBuilder builder)
        where TSink : class, IHomeostatEventSink
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Services.Any(d => d.ServiceType == typeof(EventSinkHost<TSink>)))
        {
            return builder;
        }

        builder.Services.TryAddSingleton<TSink>();
        builder.Services.AddSingleton<EventSinkHost<TSink>>();
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<EventSinkHost<TSink>>());
        builder.Services.AddSingleton<IHealingObserver>(sp => sp.GetRequiredService<EventSinkHost<TSink>>());
        return builder;
    }
}
