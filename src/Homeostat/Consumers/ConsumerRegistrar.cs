using Microsoft.Extensions.Hosting;

namespace Homeostat.Consumers;

/// <summary>Registers the consumers declared with <c>AddPausableConsumer</c> when the host starts.</summary>
internal sealed class ConsumerRegistrar(
    IHomeostatConsumers consumers, IServiceProvider services, IEnumerable<ConsumerRegistration> registrations) : IHostedService
{
    private readonly List<IDisposable> _registered = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in registrations)
        {
            _registered.Add(consumers.Register(registration.Factory(services)));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var registered in _registered)
        {
            registered.Dispose();
        }

        _registered.Clear();
        return Task.CompletedTask;
    }
}

internal sealed record ConsumerRegistration(Func<IServiceProvider, IPausableConsumer> Factory);
