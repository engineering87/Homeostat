using Microsoft.Extensions.Options;

namespace Homeostat.Guardrails;

/// <summary>Global sliding-window limit on executed actions.</summary>
internal sealed class ActionBudget
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _spent = new();
    private readonly int _max;
    private readonly TimeSpan _window;

    public ActionBudget(IOptions<HomeostatOptions> options)
    {
        _max = options.Value.MaxActionsPerWindow;
        _window = options.Value.ActionBudgetWindow;
    }

    public ActionBudgetStatus GetStatus(DateTimeOffset now)
    {
        lock (_gate)
        {
            Expire(now);
            return new ActionBudgetStatus(_spent.Count, _max, _window);
        }
    }

    public bool TryConsume(DateTimeOffset now)
    {
        lock (_gate)
        {
            Expire(now);

            if (_spent.Count >= _max)
            {
                return false;
            }

            _spent.Enqueue(now);
            return true;
        }
    }

    // Caller holds the lock.
    private void Expire(DateTimeOffset now)
    {
        while (_spent.Count > 0 && now - _spent.Peek() >= _window)
        {
            _spent.Dequeue();
        }
    }
}
