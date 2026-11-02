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

    public bool TryGet(string name, out (Action<HealingPlanBuilder> Add, Action<HealingPlanBuilder> Undo) action)
        => _actions.TryGetValue(name, out action);

    public IEnumerable<string> Names => _actions.Keys;
}
