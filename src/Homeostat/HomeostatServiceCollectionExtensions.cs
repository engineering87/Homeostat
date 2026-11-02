using Homeostat.Conditions;
using Homeostat.Detectors;
using Homeostat.Engine;
using Homeostat.Guardrails;
using Homeostat.Healing;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Homeostat;

/// <summary>Adds Homeostat to an <c>IServiceCollection</c>.</summary>
public static class HomeostatServiceCollectionExtensions
{
    /// <summary>
    /// Adds Homeostat and its background engine. Safe to call more than once:
    /// core services are registered only the first time.
    /// </summary>
    public static HomeostatBuilder AddHomeostat(this IServiceCollection services, Action<HomeostatBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<HomeostatOptions>()
            .Validate(o => o.MaxActionsPerWindow > 0, "MaxActionsPerWindow must be positive.")
            .Validate(o => o.ActionBudgetWindow > TimeSpan.Zero, "ActionBudgetWindow must be positive.")
            .Validate(o => o.MaxConcurrentHealings > 0, "MaxConcurrentHealings must be positive.")
            .Validate(o => o.DefaultCooldown >= TimeSpan.Zero, "DefaultCooldown cannot be negative.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.DefaultActionTimeout), "DefaultActionTimeout must be positive and at most about 49 days.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.DefaultVerificationTimeout), "DefaultVerificationTimeout must be positive and at most about 49 days.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.VerificationPollInterval), "VerificationPollInterval must be positive and at most about 49 days.")
            .Validate(o => o.SignalBufferCapacity > 0, "SignalBufferCapacity must be positive.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.DefaultApprovalTimeout), "DefaultApprovalTimeout must be positive and at most about 49 days.")
            .Validate(o => o.RunHistorySize >= 0, "RunHistorySize cannot be negative.")
            .Validate(o => Guardrails.TimerLimits.IsValid(o.DetectionInterval), "DetectionInterval must be positive and at most about 49 days.");

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<SignalChannel>();
        services.TryAddSingleton<ISignalPublisher>(sp => sp.GetRequiredService<SignalChannel>());
        services.TryAddSingleton(sp => CreateConditionSet(sp.GetServices<ConditionDefinition>()));
        services.TryAddSingleton<ActionBudget>();
        services.TryAddSingleton<HomeostatControl>();
        services.TryAddSingleton<IHomeostatControl>(sp => sp.GetRequiredService<HomeostatControl>());
        services.TryAddSingleton<IHomeostatSwitches, HomeostatSwitches>();
        services.TryAddSingleton<ObserverNotifier>();
        services.TryAddSingleton<RunHistory>();
        services.TryAddSingleton<SwitchOwnership>();
        services.TryAddSingleton<HomeostatApprovals>();
        services.TryAddSingleton<IHomeostatApprovals>(sp => sp.GetRequiredService<HomeostatApprovals>());
        services.TryAddSingleton<HealingRunner>();
        services.TryAddTransient<ConditionClearedVerifier>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HomeostatEngine>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DetectorHost>());

        var builder = new HomeostatBuilder(services);
        configure?.Invoke(builder);
        return builder;
    }

    private static ConditionSet CreateConditionSet(IEnumerable<ConditionDefinition> definitions)
    {
        var list = definitions.ToList();
        var duplicate = list.GroupBy(d => d.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"The condition '{duplicate.Key}' is registered more than once.");
        }

        ValidateSuppression(list);
        return new ConditionSet(list);
    }

    private static void ValidateSuppression(List<ConditionDefinition> definitions)
    {
        var byName = definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            foreach (var cause in definition.SuppressedBy)
            {
                if (string.Equals(cause, definition.Name, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"The condition '{definition.Name}' cannot suppress itself.");
                }

                if (!byName.ContainsKey(cause))
                {
                    throw new InvalidOperationException(
                        $"The condition '{definition.Name}' is suppressed by '{cause}', which is not registered.");
                }
            }
        }

        // A cycle would let two active conditions block each other forever.
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done
        foreach (var definition in definitions)
        {
            Visit(definition.Name, []);
        }

        void Visit(string name, List<string> path)
        {
            if (state.TryGetValue(name, out var s))
            {
                if (s == 1)
                {
                    var cycle = path.Skip(path.IndexOf(name)).Append(name);
                    throw new InvalidOperationException($"Suppression cycle between conditions: {string.Join(" -> ", cycle)}.");
                }

                return;
            }

            state[name] = 1;
            path.Add(name);
            foreach (var cause in byName[name].SuppressedBy)
            {
                Visit(cause, path);
            }

            path.RemoveAt(path.Count - 1);
            state[name] = 2;
        }
    }
}
