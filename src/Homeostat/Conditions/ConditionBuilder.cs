using Homeostat.Healing;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Conditions;

/// <summary>Fluent builder for a <see cref="ConditionDefinition"/>.</summary>
public sealed class ConditionBuilder
{
    private readonly string _name;
    private readonly IServiceCollection _services;
    private string? _source;
    private Func<Signal, bool>? _relevant;
    private Func<Signal, bool>? _trigger;
    private Func<Signal, bool>? _clear;
    private TimeSpan _sustainFor = TimeSpan.Zero;
    private TimeSpan _clearAfter = TimeSpan.Zero;
    private readonly List<string> _suppressedBy = [];
    private HealingPlanBuilder? _plan;

    internal ConditionBuilder(string name, IServiceCollection services)
    {
        _name = name;
        _services = services;
    }

    /// <summary>Only consider signals coming from the given source.</summary>
    public ConditionBuilder ForSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        _source = source;
        return this;
    }

    /// <summary>Only consider signals that satisfy a custom filter. Combined with <see cref="ForSource"/> if both are set.</summary>
    public ConditionBuilder Where(Func<Signal, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _relevant = filter;
        return this;
    }

    /// <summary>Trigger when a relevant signal is at least as bad as <paramref name="severity"/>.</summary>
    public ConditionBuilder WhenSeverityAtLeast(SignalSeverity severity)
    {
        _trigger = s => s.Severity >= severity;
        return this;
    }

    /// <summary>Trigger on a custom predicate.</summary>
    public ConditionBuilder When(Func<Signal, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _trigger = predicate;
        return this;
    }

    /// <summary>
    /// Clear on a custom predicate. By default a condition with a source clears on any relevant
    /// signal that does not trigger it.
    /// </summary>
    public ConditionBuilder ClearWhen(Func<Signal, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _clear = predicate;
        return this;
    }

    /// <summary>Require triggering signals to persist for <paramref name="duration"/> before activating.</summary>
    public ConditionBuilder SustainedFor(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration cannot be negative.");
        }

        _sustainFor = duration;
        return this;
    }

    /// <summary>
    /// Hysteresis: once active, the condition clears only after clearing signals persist for
    /// <paramref name="duration"/>. A triggering signal in between restarts the wait. Prevents a condition from
    /// flapping between active and cleared when a component is recovering.
    /// </summary>
    /// <remarks>
    /// The default verifier waits for the condition to clear, so keep <c>VerificationTimeout</c> longer than this
    /// duration plus one signal period.
    /// </remarks>
    public ConditionBuilder ClearAfter(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration cannot be negative.");
        }

        _clearAfter = duration;
        return this;
    }

    /// <summary>
    /// Do not heal this condition while any of the named conditions is pending or active: they are the likely cause,
    /// and this condition is a symptom. The condition is still tracked and reported. A run already in progress stops
    /// before its next step.
    /// </summary>
    /// <example>
    /// <code>
    /// .AddCondition("forecast-slow", c => c
    ///     .ForSource("http:forecast")
    ///     .SuppressedBy("network-down")
    ///     .Heal(p => p.With&lt;RenewForecastClientAction&gt;()))
    /// </code>
    /// </example>
    public ConditionBuilder SuppressedBy(params string[] conditionNames)
    {
        ArgumentNullException.ThrowIfNull(conditionNames);
        foreach (var name in conditionNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(conditionNames));
            if (!_suppressedBy.Contains(name, StringComparer.Ordinal))
            {
                _suppressedBy.Add(name);
            }
        }

        return this;
    }

    /// <summary>Define the remediation plan that runs when the condition becomes active.</summary>
    public ConditionBuilder Heal(Action<HealingPlanBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _plan = new HealingPlanBuilder(_services);
        configure(_plan);
        return this;
    }

    internal ConditionDefinition Build()
    {
        if (_source is null && _clear is null)
        {
            throw new InvalidOperationException(
                $"Condition '{_name}' must either call ForSource(...) or define ClearWhen(...); " +
                "otherwise a healthy signal from any component would clear it.");
        }

        var source = _source;
        var custom = _relevant;
        Func<Signal, bool> relevant = (source, custom) switch
        {
            (null, null) => static _ => true,
            (not null, null) => s => string.Equals(s.Source, source, StringComparison.Ordinal),
            (null, not null) => custom!,
            _ => s => string.Equals(s.Source, source, StringComparison.Ordinal) && custom!(s),
        };

        Func<Signal, bool> trigger = _trigger ?? DefaultTrigger;
        Func<Signal, bool> clear = _clear ?? (s => !trigger(s));

        return new ConditionDefinition(_name, relevant, trigger, clear, _sustainFor, _clearAfter, _suppressedBy.ToArray(), _plan?.Build(_name));
    }

    private static bool DefaultTrigger(Signal signal) => signal.Severity >= SignalSeverity.Unhealthy;
}
