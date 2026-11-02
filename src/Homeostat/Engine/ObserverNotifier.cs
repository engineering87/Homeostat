using Microsoft.Extensions.Logging;

namespace Homeostat.Engine;

/// <summary>Invokes every registered <see cref="IHealingObserver"/>, isolating their failures.</summary>
internal sealed class ObserverNotifier
{
    private readonly IHealingObserver[] _observers;
    private readonly ILogger<ObserverNotifier> _logger;

    public ObserverNotifier(IEnumerable<IHealingObserver> observers, ILogger<ObserverNotifier> logger)
    {
        _observers = observers.ToArray();
        _logger = logger;
    }

    public void Notify(Action<IHealingObserver> callback)
    {
        foreach (var observer in _observers)
        {
            try
            {
                callback(observer);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Healing observer {Observer} threw an exception.", observer.GetType().Name);
            }
        }
    }
}
