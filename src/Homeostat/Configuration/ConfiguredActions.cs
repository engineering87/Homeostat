using Homeostat.Healing;

namespace Homeostat.Configuration;

/// <summary>Actions that conditions defined in configuration can refer to by name.</summary>
internal sealed class ConfiguredActions
{
    private readonly Dictionary<string, (Action<HealingPlanBuilder> Add, Action<HealingPlanBuilder> Undo)> _actions =
        new(StringComparer.OrdinalIgnoreCase);

    public void Map(string name, Action<HealingPlanBuilder> add, Action<HealingPlanBuilder> undo)
    {
        if (name.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Action names cannot contain ':', which is reserved for built-in actions such as 'switch:name'.", nameof(name));
        }

        _actions[name] = (add, undo);
    }

    /// <summary>
    /// Maps action names that were not mapped, when set. Replays use it to evaluate a configuration without the
    /// application's action classes: a replay never executes actions, it only needs their names.
    /// </summary>
    public Func<string, (Action<HealingPlanBuilder> Add, Action<HealingPlanBuilder> Undo)>? Fallback { get; set; }

    public bool TryGet(string name, out (Action<HealingPlanBuilder> Add, Action<HealingPlanBuilder> Undo) action)
    {
        if (_actions.TryGetValue(name, out action))
        {
            return true;
        }

        if (Fallback is { } fallback)
        {
            action = fallback(name);
            return true;
        }

        return false;
    }

    public IEnumerable<string> Names => _actions.Keys;
}
