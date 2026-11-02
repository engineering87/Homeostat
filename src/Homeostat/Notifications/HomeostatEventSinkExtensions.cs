using Homeostat.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Homeostat;

/// <summary>Registers <see cref="HomeostatEventSink"/> implementations.</summary>
public static class HomeostatEventSinkExtensions
{
    /// <summary>
    /// Registers <typeparamref name="TSink"/> once, as a singleton that is both a hosted service and an observer.
    /// Registering the same type again has no effect.
    /// </summary>
    /// <typeparam name="TSink">The sink type.</typeparam>
    /// <param name="builder">The Homeostat builder.</param>
    public static HomeostatBuilder AddEventSink<TSink>(this HomeostatBuilder builder)
        where TSink : HomeostatEventSink
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Services.Any(d => d.ServiceType == typeof(TSink)))
        {
            return builder;
        }

        builder.Services.TryAddSingleton<TSink>();
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TSink>());
        builder.AddObserver<TSink>();
        return builder;
    }
}
