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
    private readonly List<Action<Signal>> _recorders = [];
    private (int Count, TimeSpan Within)? _occurrences;
    private CompositeMode? _composite;
    private string[] _dependsOn = [];
    private HealingPlanBuilder? _plan;

    internal ConditionBuilder(string name, IServiceCollection services)
    {
        _name = name;
        _services = services;
    }

    /// <summary>Only consider signals coming from the given source.</summary>
    public ConditionBuilder ForSource(string source)
    {
        EnsureNotComposite(nameof(ForSource));
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        _source = source;
        return this;
    }

    /// <summary>Only consider signals that satisfy a custom filter. Combined with <see cref="ForSource"/> if both are set.</summary>
    public ConditionBuilder Where(Func<Signal, bool> filter)
    {
        EnsureNotComposite(nameof(Where));
        ArgumentNullException.ThrowIfNull(filter);
        _relevant = filter;
        return this;
    }

    /// <summary>Trigger when a relevant signal is at least as bad as <paramref name="severity"/>.</summary>
    public ConditionBuilder WhenSeverityAtLeast(SignalSeverity severity)
    {
        EnsureNotComposite(nameof(WhenSeverityAtLeast));
        _trigger = s => s.Severity >= severity;
        return this;
    }

    /// <summary>Trigger on a custom predicate.</summary>
    public ConditionBuilder When(Func<Signal, bool> predicate)
    {
        EnsureNotComposite(nameof(When));
        ArgumentNullException.ThrowIfNull(predicate);
        _trigger = predicate;
        return this;
    }

    /// <summary>Trigger when a relevant signal carries a <see cref="Signal.Value"/> at or above <paramref name="threshold"/>.</summary>
    /// <param name="threshold">Threshold, in the unit of the signal's value.</param>
    public ConditionBuilder WhenValueAbove(double threshold)
    {
        EnsureNotComposite(nameof(WhenValueAbove));
        _trigger = s => s.Value >= threshold;
        return this;
    }

    /// <summary>Trigger when a relevant signal carries a <see cref="Signal.Value"/> at or below <paramref name="threshold"/>.</summary>
    /// <param name="threshold">Threshold, in the unit of the signal's value.</param>
    public ConditionBuilder WhenValueBelow(double threshold)
    {
        EnsureNotComposite(nameof(WhenValueBelow));
        _trigger = s => s.Value <= threshold;
        return this;
    }

    /// <summary>
    /// Trigger when the values of the relevant signals rise faster than <paramref name="perMinute"/> units per minute,
    /// measured by linear regression over <paramref name="window"/>. Catches a slow leak or a growing backlog before it
    /// crosses a fixed threshold. The values must span at least half the window, so a short burst (such as the
    /// warm-up after startup) is not taken for a trend.
    /// </summary>
    /// <param name="perMinute">Slope threshold, in units of the value per minute.</param>
    /// <param name="window">How far back to look.</param>
    /// <param name="minimumSamples">Values needed before the rule can trigger (at least 3).</param>
    /// <example><code>c.ForSource("memory").WhenRisingFasterThan(0.01, TimeSpan.FromMinutes(30))   // +1% load per minute</code></example>
    public ConditionBuilder WhenRisingFasterThan(double perMinute, TimeSpan window, int minimumSamples = 5)
        => Trend(window, minimumSamples, slope => slope > perMinute);

    /// <summary>Trigger when the values of the relevant signals fall faster than <paramref name="perMinute"/> units per minute.</summary>
    /// <param name="perMinute">Slope threshold, as a positive number of units per minute.</param>
    /// <param name="window">How far back to look.</param>
    /// <param name="minimumSamples">Values needed before the rule can trigger (at least 3).</param>
    public ConditionBuilder WhenFallingFasterThan(double perMinute, TimeSpan window, int minimumSamples = 5)
        => Trend(window, minimumSamples, slope => slope < -perMinute);

    /// <summary>
    /// Trigger when a value is unusually high compared with the recent past: at least <paramref name="zScore"/> standard
    /// deviations above the mean of the values within <paramref name="window"/>. No fixed threshold to choose.
    /// </summary>
    /// <param name="zScore">How many standard deviations count as anomalous.</param>
    /// <param name="window">History used as the baseline.</param>
    /// <param name="minimumHistory">Earlier values needed before the rule can trigger.</param>
    /// <remarks>
    /// A level that stays high becomes the new baseline over time, so the condition eventually clears even if the value
    /// does not go back: combine it with a fixed threshold when that matters.
    /// </remarks>
    public ConditionBuilder WhenAnomalouslyHigh(double zScore, TimeSpan window, int minimumHistory = 30)
        => Anomaly(window, minimumHistory, z => z >= zScore);

    /// <summary>Trigger when a value is unusually low compared with the recent past (see <see cref="WhenAnomalouslyHigh"/>).</summary>
    /// <param name="zScore">How many standard deviations count as anomalous.</param>
    /// <param name="window">History used as the baseline.</param>
    /// <param name="minimumHistory">Earlier values needed before the rule can trigger.</param>
    public ConditionBuilder WhenAnomalouslyLow(double zScore, TimeSpan window, int minimumHistory = 30)
        => Anomaly(window, minimumHistory, z => z <= -zScore);

    /// <summary>
    /// Trigger only when the trigger rule matched at least <paramref name="count"/> times within <paramref name="within"/>,
    /// not necessarily in a row: for problems that come and go, such as intermittent failures.
    /// </summary>
    /// <param name="count">Matches needed.</param>
    /// <param name="within">Window in which they must happen.</param>
    public ConditionBuilder Occurrences(int count, TimeSpan within)
    {
        EnsureNotComposite(nameof(Occurrences));
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        if (within <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(within), "The window must be positive.");
        }

        _occurrences = (count, within);
        return this;
    }

    /// <summary>
    /// Makes this a composite condition: active while every listed condition is active. Use it to heal a combination
    /// differently from its parts, for example when the database is slow <i>and</i> the cache is down. The condition
    /// does not listen to signals itself, so it needs no source.
    /// </summary>
    /// <param name="conditionNames">Conditions to combine.</param>
    public ConditionBuilder WhenAllActive(params string[] conditionNames) => Composite(CompositeMode.All, conditionNames);

    /// <summary>Makes this a composite condition: active while at least one listed condition is active.</summary>
    /// <param name="conditionNames">Conditions to combine.</param>
    public ConditionBuilder WhenAnyActive(params string[] conditionNames) => Composite(CompositeMode.Any, conditionNames);

    /// <summary>
    /// Clear on a custom predicate. By default a condition with a source clears on any relevant
    /// signal that does not trigger it.
    /// </summary>
    public ConditionBuilder ClearWhen(Func<Signal, bool> predicate)
    {
        EnsureNotComposite(nameof(ClearWhen));
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

    /// <summary>Source of the internal signals that drive a composite condition.</summary>
    internal static string CompositeSource(string name) => $"homeostat:composite:{name}";

    private ConditionBuilder Trend(TimeSpan window, int minimumSamples, Func<double, bool> rule)
    {
        EnsureNotComposite("a trend rule");
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "The window must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(minimumSamples, 3);
        var series = new SignalSeries(window);
        _recorders.Add(series.Add);
        _trigger = s => HasValue(s) && series.SlopePerMinute(minimumSamples) is { } slope && rule(slope);
        return this;
    }

    private ConditionBuilder Anomaly(TimeSpan window, int minimumHistory, Func<double, bool> rule)
    {
        EnsureNotComposite("an anomaly rule");
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "The window must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(minimumHistory, 2);
        var series = new SignalSeries(window);
        _recorders.Add(series.Add);
        _trigger = s => HasValue(s) && series.LatestZScore(minimumHistory) is { } z && rule(z);
        return this;
    }

    private ConditionBuilder Composite(CompositeMode mode, string[] conditionNames)
    {
        ArgumentNullException.ThrowIfNull(conditionNames);
        if (conditionNames.Length == 0)
        {
            throw new ArgumentException("List at least one condition.", nameof(conditionNames));
        }

        foreach (var name in conditionNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(conditionNames));
        }

        if (_source is not null || _relevant is not null || _trigger is not null || _clear is not null || _occurrences is not null)
        {
            throw new InvalidOperationException(
                $"Condition '{_name}': WhenAllActive/WhenAnyActive replace the source and the rules; call them on a condition without ForSource, Where, When... or ClearWhen.");
        }

        _composite = mode;
        _dependsOn = conditionNames.Distinct(StringComparer.Ordinal).ToArray();
        _source = CompositeSource(_name);
        _trigger = static s => s.Severity >= SignalSeverity.Unhealthy;
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
        var recorders = new List<Action<Signal>>(_recorders);

        if (_occurrences is { } occurrences)
        {
            // Count the matches of the trigger rule within the window; trigger on the one that reaches the count.
            var baseTrigger = trigger;
            var matches = new Queue<DateTimeOffset>();
            var lastMatched = false;
            recorders.Add(s =>
            {
                lastMatched = baseTrigger(s);
                if (lastMatched)
                {
                    matches.Enqueue(s.Timestamp);
                }

                while (matches.Count > 0 && s.Timestamp - matches.Peek() > occurrences.Within)
                {
                    matches.Dequeue();
                }
            });
            trigger = _ => lastMatched && matches.Count >= occurrences.Count;

            // Over only when the matches within the window drop below the count, not at the first good signal.
            _clear ??= _ => matches.Count < occurrences.Count;
        }

        Func<Signal, bool> clear = _clear ?? (s => !trigger(s));
        Action<Signal>? record = recorders.Count switch
        {
            0 => null,
            1 => recorders[0],
            _ => s =>
            {
                foreach (var recorder in recorders)
                {
                    recorder(s);
                }
            },
        };

        return new ConditionDefinition(
            _name, relevant, trigger, clear, _sustainFor, _clearAfter, _suppressedBy.ToArray(), _plan?.Build(_name),
            record, _composite, _dependsOn);
    }

    private static bool DefaultTrigger(Signal signal) => signal.Severity >= SignalSeverity.Unhealthy;

    private static bool HasValue(Signal signal) => signal.Value is { } value && double.IsFinite(value);

    private void EnsureNotComposite(string method)
    {
        if (_composite is not null)
        {
            throw new InvalidOperationException(
                $"Condition '{_name}' is a composite (WhenAllActive/WhenAnyActive): {method} does not apply to it.");
        }
    }
}
