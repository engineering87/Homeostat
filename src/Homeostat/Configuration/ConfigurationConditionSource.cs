using System.Globalization;
using Homeostat.Conditions;
using Homeostat.Healing;
using Homeostat.Signals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Configuration;

/// <summary>Builds conditions from a configuration section, again whenever the configuration changes.</summary>
internal sealed class ConfigurationConditionSource
{
    private static readonly HashSet<string> ConditionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Source", "Severity", "ValueAbove", "ValueBelow", "RisingFasterThan", "FallingFasterThan", "AnomalouslyHigh",
        "AnomalouslyLow", "ClearWhenHealthy", "SustainedFor", "ClearAfter", "SuppressedBy", "Occurrences", "AllActive",
        "AnyActive", "Plan",
    };

    /// <summary>Settings that decide when a signal triggers the condition: at most one per condition.</summary>
    private static readonly string[] TriggerKeys =
    [
        "Severity", "ValueAbove", "ValueBelow", "RisingFasterThan", "FallingFasterThan", "AnomalouslyHigh", "AnomalouslyLow",
    ];

    private static readonly HashSet<string> TrendKeys = new(StringComparer.OrdinalIgnoreCase) { "PerMinute", "Window", "MinimumSamples" };

    private static readonly HashSet<string> AnomalyKeys = new(StringComparer.OrdinalIgnoreCase) { "ZScore", "Window", "MinimumHistory" };

    private static readonly HashSet<string> PlanKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steps", "Cooldown", "CooldownBackoff", "ActionTimeout", "VerificationTimeout", "Scope",
    };

    private static readonly HashSet<string> StepKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Action", "RequireApproval", "ApprovalTimeout", "UndoAction", "TurnOffWhenCleared",
    };

    private readonly ConfiguredActions _actions;
    private readonly object _gate = new();

    // Definitions by name with the configuration they were built from: an unchanged condition keeps its definition
    // (and the history of its stateful rules) across reloads.
    private Dictionary<string, (string Fingerprint, ConditionDefinition Definition)> _built = new(StringComparer.Ordinal);

    public ConfigurationConditionSource(IConfiguration section, ConfiguredActions actions)
    {
        Section = section;
        _actions = actions;
    }

    public IConfiguration Section { get; }

    public IReadOnlyList<ConditionDefinition> Load()
    {
        lock (_gate)
        {
            var built = new Dictionary<string, (string Fingerprint, ConditionDefinition Definition)>(StringComparer.Ordinal);
            foreach (var condition in Section.GetChildren())
            {
                var fingerprint = Fingerprint(condition);
                var definition = _built.TryGetValue(condition.Key, out var previous) && previous.Fingerprint == fingerprint
                    ? previous.Definition
                    : Parse(condition);
                built[condition.Key] = (fingerprint, definition);
            }

            // Commit only when everything parsed, so a broken change does not replace good definitions.
            _built = built;
            return built.Values.Select(v => v.Definition).ToArray();
        }
    }

    private static string Fingerprint(IConfigurationSection section)
        => string.Join('\n', section.AsEnumerable(makePathsRelative: true)
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{p.Key}={p.Value}"));

    private static void CheckKeys(IConfigurationSection section, HashSet<string> allowed)
    {
        foreach (var child in section.GetChildren())
        {
            if (!allowed.Contains(child.Key))
            {
                throw new InvalidOperationException(
                    $"Configuration '{child.Path}' is not a known setting. Known settings here: {string.Join(", ", allowed.Order(StringComparer.OrdinalIgnoreCase))}.");
            }
        }
    }

    private ConditionDefinition Parse(IConfigurationSection section)
    {
        var name = section.Key;

        // Action classes are registered by MapAction; the builder needs a collection, not the live container.
        var builder = new ConditionBuilder(name, new ServiceCollection());

        try
        {
            CheckKeys(section, ConditionKeys);

            var composite = Strings(section, "AllActive") is not null || Strings(section, "AnyActive") is not null;
            if (composite && TriggerKeys.Concat(["Source", "ClearWhenHealthy", "Occurrences"]).Any(k => section.GetSection(k).Exists()))
            {
                throw new InvalidOperationException("a composite condition (AllActive or AnyActive) cannot have a Source or trigger and clear rules.");
            }

            if (TriggerKeys.Count(k => section.GetSection(k).Exists()) > 1)
            {
                throw new InvalidOperationException($"use only one of {string.Join(", ", TriggerKeys)}.");
            }

            if (Strings(section, "AllActive") is { Length: > 0 } all)
            {
                builder.WhenAllActive(all);
            }
            else if (Strings(section, "AnyActive") is { Length: > 0 } any)
            {
                builder.WhenAnyActive(any);
            }

            if (section["Source"] is { Length: > 0 } source)
            {
                builder.ForSource(source);
            }

            if (section["Severity"] is { Length: > 0 } severity)
            {
                builder.WhenSeverityAtLeast(Enum.Parse<SignalSeverity>(severity, ignoreCase: true));
            }

            if (Double(section, "ValueAbove") is { } above)
            {
                builder.WhenValueAbove(above);
            }

            if (Double(section, "ValueBelow") is { } below)
            {
                builder.WhenValueBelow(below);
            }

            if (Trend(section, "RisingFasterThan") is { } rising)
            {
                builder.WhenRisingFasterThan(rising.PerMinute, rising.Window, rising.MinimumSamples ?? 5);
            }

            if (Trend(section, "FallingFasterThan") is { } falling)
            {
                builder.WhenFallingFasterThan(falling.PerMinute, falling.Window, falling.MinimumSamples ?? 5);
            }

            if (Anomaly(section, "AnomalouslyHigh") is { } high)
            {
                builder.WhenAnomalouslyHigh(high.ZScore, high.Window, high.MinimumHistory ?? 30);
            }

            if (Anomaly(section, "AnomalouslyLow") is { } low)
            {
                builder.WhenAnomalouslyLow(low.ZScore, low.Window, low.MinimumHistory ?? 30);
            }

            if (Bool(section, "ClearWhenHealthy") == true)
            {
                builder.ClearWhen(s => s.Severity == SignalSeverity.Healthy);
            }

            if (Duration(section, "SustainedFor") is { } sustained)
            {
                builder.SustainedFor(sustained);
            }

            if (Duration(section, "ClearAfter") is { } clearAfter)
            {
                builder.ClearAfter(clearAfter);
            }

            if (Strings(section, "SuppressedBy") is { Length: > 0 } causes)
            {
                builder.SuppressedBy(causes);
            }

            var occurrences = section.GetSection("Occurrences");
            if (occurrences.Exists())
            {
                CheckKeys(occurrences, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Count", "Within" });
                builder.Occurrences(
                    Int(occurrences, "Count") ?? throw Missing(occurrences, "Count"),
                    Duration(occurrences, "Within") ?? throw Missing(occurrences, "Within"));
            }

            var plan = section.GetSection("Plan");
            if (plan.Exists())
            {
                builder.Heal(p => ParsePlan(plan, p));
            }

            return builder.Build();
        }
        catch (Exception ex) when (ex is not InvalidOperationException || !ex.Message.StartsWith("Configuration ", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Configuration '{section.Path}' is not a valid condition: {ex.Message}", ex);
        }
    }

    private static (double PerMinute, TimeSpan Window, int? MinimumSamples)? Trend(IConfigurationSection condition, string key)
    {
        var section = condition.GetSection(key);
        if (!section.Exists())
        {
            return null;
        }

        CheckKeys(section, TrendKeys);
        return (Double(section, "PerMinute") ?? throw Missing(section, "PerMinute"),
            Duration(section, "Window") ?? throw Missing(section, "Window"),
            Int(section, "MinimumSamples"));
    }

    private static (double ZScore, TimeSpan Window, int? MinimumHistory)? Anomaly(IConfigurationSection condition, string key)
    {
        var section = condition.GetSection(key);
        if (!section.Exists())
        {
            return null;
        }

        CheckKeys(section, AnomalyKeys);
        return (Double(section, "ZScore") ?? throw Missing(section, "ZScore"),
            Duration(section, "Window") ?? throw Missing(section, "Window"),
            Int(section, "MinimumHistory"));
    }

    private void ParsePlan(IConfigurationSection plan, HealingPlanBuilder builder)
    {
        CheckKeys(plan, PlanKeys);
        foreach (var step in plan.GetSection("Steps").GetChildren())
        {
            CheckKeys(step, StepKeys);
            var action = step["Action"] ?? throw Missing(step, "Action");
            AddAction(step, action, builder);

            if (Bool(step, "RequireApproval") == true)
            {
                builder.RequireApproval(Duration(step, "ApprovalTimeout"));
            }

            if (step["UndoAction"] is { Length: > 0 } undo)
            {
                if (!_actions.TryGet(undo, out var mapped))
                {
                    throw Unknown(step, undo);
                }

                mapped.Undo(builder);
            }
        }

        if (Duration(plan, "Cooldown") is { } cooldown)
        {
            builder.Cooldown(cooldown);
        }

        var backoff = plan.GetSection("CooldownBackoff");
        if (backoff.Exists())
        {
            CheckKeys(backoff, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Multiplier", "Max" });
            builder.CooldownBackoff(
                Double(backoff, "Multiplier") ?? throw Missing(backoff, "Multiplier"),
                Duration(backoff, "Max") ?? throw Missing(backoff, "Max"));
        }

        if (Duration(plan, "ActionTimeout") is { } actionTimeout)
        {
            builder.ActionTimeout(actionTimeout);
        }

        if (Duration(plan, "VerificationTimeout") is { } verificationTimeout)
        {
            builder.VerificationTimeout(verificationTimeout);
        }

        if (plan["Scope"] is { Length: > 0 } scope && Enum.Parse<PlanScope>(scope, ignoreCase: true) == PlanScope.Cluster)
        {
            builder.OncePerCluster();
        }
    }

    private void AddAction(IConfigurationSection step, string action, HealingPlanBuilder builder)
    {
        if (action.StartsWith("switch:", StringComparison.OrdinalIgnoreCase))
        {
            builder.TurnOnSwitch(action["switch:".Length..], Bool(step, "TurnOffWhenCleared") == true);
        }
        else if (string.Equals(action, "reload-configuration", StringComparison.OrdinalIgnoreCase))
        {
            builder.ReloadConfiguration();
        }
        else if (_actions.TryGet(action, out var mapped))
        {
            mapped.Add(builder);
        }
        else
        {
            throw Unknown(step, action);
        }
    }

    private InvalidOperationException Unknown(IConfigurationSection step, string action)
        => new($"Configuration '{step.Path}': unknown action '{action}'. Known actions: switch:<name>, reload-configuration" +
            (_actions.Names.Any() ? ", " + string.Join(", ", _actions.Names.Order()) : string.Empty) +
            ". Map action classes with MapAction<T>(name).");

    private static InvalidOperationException Missing(IConfigurationSection section, string key)
        => new($"Configuration '{section.Path}:{key}' is required.");

    private static string[]? Strings(IConfigurationSection section, string key)
    {
        var child = section.GetSection(key);
        if (child.Value is { Length: > 0 } single)
        {
            return single.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        var values = child.GetChildren().Select(c => c.Value).OfType<string>().Where(v => v.Length > 0).ToArray();
        return values.Length > 0 ? values : null;
    }

    private static double? Double(IConfigurationSection section, string key)
        => section[key] is { Length: > 0 } text ? double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) : null;

    private static int? Int(IConfigurationSection section, string key)
        => section[key] is { Length: > 0 } text ? int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) : null;

    private static bool? Bool(IConfigurationSection section, string key)
        => section[key] is { Length: > 0 } text ? bool.Parse(text) : null;

    /// <summary>Accepts <c>hh:mm:ss</c> or a number with a unit: <c>500ms</c>, <c>30s</c>, <c>5m</c>, <c>2h</c>, <c>1d</c>.</summary>
    internal static TimeSpan? Duration(IConfigurationSection section, string key)
    {
        if (section[key] is not { Length: > 0 } text)
        {
            return null;
        }

        text = text.Trim();
        (string Suffix, Func<double, TimeSpan> Make)[] units =
        [
            ("ms", TimeSpan.FromMilliseconds),
            ("s", TimeSpan.FromSeconds),
            ("m", TimeSpan.FromMinutes),
            ("h", TimeSpan.FromHours),
            ("d", TimeSpan.FromDays),
        ];

        foreach (var (suffix, make) in units)
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && double.TryParse(text[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                return make(amount);
            }
        }

        if (!text.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Configuration '{section.Path}:{key}' = '{text}' needs a unit (500ms, 30s, 5m, 2h, 1d) or the hh:mm:ss format.");
        }

        return TimeSpan.Parse(text, CultureInfo.InvariantCulture);
    }
}
