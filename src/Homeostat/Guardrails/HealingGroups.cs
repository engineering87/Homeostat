using Microsoft.Extensions.Options;

namespace Homeostat.Guardrails;

/// <summary>Validation of healing group names, shared by conditions, configuration, guardrails, pauses and windows.</summary>
internal static class HealingGroupNames
{
    /// <summary>Longest group name accepted, like a switch name: it becomes a store key, a metric tag and a route value.</summary>
    public const int MaxLength = 200;

    /// <summary>
    /// Like a condition name, not empty; like a name in a control route, without control characters (no line breaks in
    /// logs); and, like a switch name, at most <see cref="MaxLength"/> characters.
    /// </summary>
    public static bool IsValid(string? group)
        => !string.IsNullOrWhiteSpace(group) && group.Length <= MaxLength && !group.Any(char.IsControl);

    /// <summary>Throws an <see cref="ArgumentException"/> for a name <see cref="IsValid"/> refuses.</summary>
    public static string Validate(string? group, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group, parameterName);
        if (!IsValid(group))
        {
            throw new ArgumentException(
                $"A healing group name must have at most {MaxLength} characters and no control characters.", parameterName);
        }

        return group;
    }
}

/// <summary>A group named by <see cref="HomeostatBuilder.ConfigureGroup"/>, one registration per group.</summary>
internal sealed record HealingGroupRegistration(string Name);

/// <summary>The guardrails of one configured group, fixed at startup.</summary>
internal sealed class HealingGroup
{
    public HealingGroup(string name, HealingGroupOptions options, HomeostatOptions global)
    {
        Name = name;
        MaxActionsPerWindow = options.MaxActionsPerWindow;
        ActionBudgetWindow = options.ActionBudgetWindow ?? global.ActionBudgetWindow;
        MaxConcurrentHealings = options.MaxConcurrentHealings;

        // Not disposed, like the global semaphore: runs may outlive the host, and its wait handle is never used.
        Concurrency = options.MaxConcurrentHealings is { } limit ? new SemaphoreSlim(limit, limit) : null;
    }

    public string Name { get; }

    /// <summary>The group's own budget, or <c>null</c>.</summary>
    public int? MaxActionsPerWindow { get; }

    /// <summary>Window of the group's budget (the global one when the group does not set it).</summary>
    public TimeSpan ActionBudgetWindow { get; }

    public int? MaxConcurrentHealings { get; }

    /// <summary>The group's concurrency slots, or <c>null</c> without a limit of its own.</summary>
    public SemaphoreSlim? Concurrency { get; }
}

/// <summary>The configured healing groups (see <see cref="HomeostatBuilder.ConfigureGroup"/>), read once at startup.</summary>
internal sealed class HealingGroups
{
    private readonly Dictionary<string, HealingGroup> _groups;

    public HealingGroups(IEnumerable<HealingGroupRegistration> registrations, IOptionsMonitor<HealingGroupOptions> options, IOptions<HomeostatOptions> global)
    {
        _groups = registrations
            .Select(r => r.Name)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, name => new HealingGroup(name, options.Get(name), global.Value), StringComparer.Ordinal);
    }

    /// <summary>The configured groups, by name.</summary>
    public IReadOnlyCollection<string> Configured => _groups.Keys;

    /// <summary>The guardrails of a group, or <c>null</c> when it has none (no group, or not configured).</summary>
    public HealingGroup? Get(string? group) => group is not null && _groups.TryGetValue(group, out var found) ? found : null;
}
