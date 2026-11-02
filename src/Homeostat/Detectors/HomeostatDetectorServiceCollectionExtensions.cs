using Homeostat.Detectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Homeostat;

/// <summary>Registers custom detectors on an <c>IServiceCollection</c>.</summary>
public static class HomeostatDetectorServiceCollectionExtensions
{
    /// <summary>
    /// Registers a detector that runs at <see cref="HomeostatOptions.DetectionInterval"/>.
    /// Requires <c>AddHomeostat</c>. Registering the same detector type twice has no effect.
    /// </summary>
    public static IServiceCollection AddSignalDetector<TDetector>(this IServiceCollection services)
        where TDetector : class, ISignalDetector
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISignalDetector, TDetector>());
        return services;
    }
}
