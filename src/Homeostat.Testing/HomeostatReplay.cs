using System.Globalization;
using System.Text;
using Homeostat.Conditions;
using Homeostat.Configuration;
using Homeostat.Coordination;
using Homeostat.Detectors;
using Homeostat.Engine;
using Homeostat.Healing;
using Homeostat.Persistence;
using Homeostat.Recording;
using Homeostat.Signals;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Homeostat.Testing;

/// <summary>
/// Replays recorded signals against a set of conditions and plans: "what would the plans have done yesterday?".
/// Use it to try a new threshold, a new condition or a new plan on real traffic before deploying it.
/// </summary>
/// <remarks>
/// A replay never acts: it runs in dry-run (enforced after the configuration passed in), on simulated time following the
/// signal timestamps, with an in-memory store and coordinator, and without detectors, notifiers, audit log, logging
/// providers, traces, metrics or other hosted services, whatever the configuration passed in registers. Actions are never
/// created.
/// </remarks>
/// <example>
/// <code>
/// var report = await HomeostatReplay.RunFilesAsync(
///     Directory.GetFiles("signals", "homeostat-signals-2026-11-01.jsonl"),
///     h => h.AddCondition("feed-down", c => c.ForSource("weather-feed").SustainedFor(TimeSpan.FromSeconds(30))
///         .Heal(p => p.With&lt;RenewFeedClientAction&gt;())));
/// Console.WriteLine(report.ToText());
/// </code>
/// </example>
public static class HomeostatReplay
{
    /// <summary>
    /// Replays the signals of recording files (written by <c>AddSignalRecording</c>) in time order, merging the files as they
    /// are read: the files of several instances, or of several days, form one timeline. Files are streamed, so long
    /// recordings do not need to fit in memory.
    /// </summary>
    /// <param name="paths">Recording files.</param>
    /// <param name="configure">Conditions and plans to evaluate, as in <c>AddHomeostat</c>.</param>
    /// <param name="cancellationToken">Stops the replay.</param>
    public static Task<ReplayReport> RunFilesAsync(IEnumerable<string> paths, Action<HomeostatBuilder> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var files = paths.ToList();
        return ReplayAsync(Merge(files.Select(f => Reorder(RecordedSignal.Read(f)))), configure, cancellationToken);
    }

    /// <summary>Signals the recorder wrote slightly out of order (detectors run concurrently), put back in time order.</summary>
    private static IEnumerable<RecordedSignal> Reorder(IEnumerable<RecordedSignal> signals, int window = 1000)
    {
        var buffer = new PriorityQueue<RecordedSignal, DateTimeOffset>();
        foreach (var signal in signals)
        {
            buffer.Enqueue(signal, signal.Timestamp);
            if (buffer.Count > window)
            {
                yield return buffer.Dequeue();
            }
        }

        while (buffer.Count > 0)
        {
            yield return buffer.Dequeue();
        }
    }

    /// <summary>Merges streams in time order, reading each one as needed.</summary>
    private static IEnumerable<RecordedSignal> Merge(IEnumerable<IEnumerable<RecordedSignal>> streams)
    {
        var heads = new PriorityQueue<IEnumerator<RecordedSignal>, DateTimeOffset>();
        var all = new List<IEnumerator<RecordedSignal>>();
        try
        {
            foreach (var stream in streams)
            {
                var enumerator = stream.GetEnumerator();
                all.Add(enumerator);
                if (enumerator.MoveNext())
                {
                    heads.Enqueue(enumerator, enumerator.Current.Timestamp);
                }
            }

            while (heads.TryDequeue(out var head, out _))
            {
                yield return head.Current;
                if (head.MoveNext())
                {
                    heads.Enqueue(head, head.Current.Timestamp);
                }
            }
        }
        finally
        {
            foreach (var enumerator in all)
            {
                enumerator.Dispose();
            }
        }
    }

    /// <summary>
    /// Replays recording files against conditions and plans defined in configuration (the <c>Conditions</c> section, see
    /// <c>AddConditionsFromConfiguration</c>), without the application's code: actions are only names in a replay, so
    /// names that are not mapped are accepted. This is what <c>homeostat replay</c> runs.
    /// </summary>
    /// <param name="paths">Recording files.</param>
    /// <param name="conditions">The conditions section, for example <c>configuration.GetSection("Homeostat:Conditions")</c>.</param>
    /// <param name="options">
    /// The application's <see cref="HomeostatOptions"/> (cooldown, budget, concurrency), which change how often plans
    /// would run; dry-run is forced on whatever it sets.
    /// </param>
    /// <param name="cancellationToken">Stops the replay.</param>
    public static Task<ReplayReport> RunFilesAsync(
        IEnumerable<string> paths, IConfiguration conditions, Action<HomeostatOptions>? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        return RunFilesAsync(paths, h => FromConfiguration(h, conditions, options), cancellationToken);
    }

    /// <summary>Replays signals against conditions and plans defined in configuration (see the other overload).</summary>
    /// <param name="signals">Signals to replay.</param>
    /// <param name="conditions">The conditions section.</param>
    /// <param name="options">The application's options; dry-run is forced on.</param>
    /// <param name="cancellationToken">Stops the replay.</param>
    public static Task<ReplayReport> RunAsync(
        IEnumerable<RecordedSignal> signals, IConfiguration conditions, Action<HomeostatOptions>? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conditions);
        return RunAsync(signals, h => FromConfiguration(h, conditions, options), cancellationToken);
    }

    private static void FromConfiguration(HomeostatBuilder builder, IConfiguration conditions, Action<HomeostatOptions>? options)
    {
        if (options is not null)
        {
            builder.Configure(options);
        }

        // The catalog of mapped actions, found by AddConditionsFromConfiguration when the conditions are loaded.
        var catalog = builder.Services.Select(d => d.ImplementationInstance).OfType<ConfiguredActions>().FirstOrDefault();
        if (catalog is null)
        {
            catalog = new ConfiguredActions();
            builder.Services.AddSingleton(catalog);
        }

        builder.AddConditionsFromConfiguration(conditions);
        catalog.Fallback = name => (
            plan => plan.With(name, static (_, _) => Task.CompletedTask),
            plan => plan.UndoWith(name, static (_, _) => Task.CompletedTask));
    }

    /// <summary>Replays signals, in time order.</summary>
    /// <param name="signals">Signals to replay.</param>
    /// <param name="configure">Conditions and plans to evaluate, as in <c>AddHomeostat</c>.</param>
    /// <param name="cancellationToken">Stops the replay.</param>
    public static Task<ReplayReport> RunAsync(IEnumerable<RecordedSignal> signals, Action<HomeostatBuilder> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signals);
        return ReplayAsync(signals.OrderBy(s => s.Timestamp), configure, cancellationToken);
    }

    private static async Task<ReplayReport> ReplayAsync(
        IEnumerable<RecordedSignal> ordered, Action<HomeostatBuilder> configure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configure);

        using var signals = ordered.GetEnumerator();
        if (!signals.MoveNext())
        {
            return new ReplayReport(DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0, [], [], []);
        }

        var first = signals.Current;
        var time = new FakeTimeProvider(first.Timestamp);
        var recorder = new ReplayRecorder(time);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHomeostat(configure);

        // Whatever the configuration registered: simulated time, dry-run (applied last, after any other
        // configuration), nothing real (no detectors, sinks, hosted services, logging providers, shared store or
        // recording).
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(time);
        services.PostConfigure<HomeostatOptions>(o => o.DryRun = true);
        services.RemoveAll<ISignalDetector>();
        services.RemoveAll<IHostedService>();
        services.RemoveAll<IHealingObserver>();
        services.RemoveAll<SignalRecorder>();
        services.RemoveAll<ILoggerProvider>();
        services.Replace(ServiceDescriptor.Singleton<IHomeostatStore>(new InMemoryHomeostatStore()));
        services.Replace(ServiceDescriptor.Singleton<IHomeostatCoordinator, InMemoryCoordinator>());
        services.AddSingleton<IHostedService, HomeostatEngine>();
        services.AddSingleton<IHealingObserver>(recorder);

        await using var provider = services.BuildServiceProvider();
        if (!provider.GetRequiredService<IOptions<HomeostatOptions>>().Value.DryRun)
        {
            throw new InvalidOperationException("A replay must run in dry-run, but the configuration turned dry-run off after it.");
        }

        var channel = provider.GetRequiredService<SignalChannel>();
        var runner = provider.GetRequiredService<HealingRunner>();
        runner.TelemetryEnabled = false;
        provider.GetRequiredService<SeasonalBaselineKeeper>().SkipLoading();   // learns from the replayed signals only
        var conditions = provider.GetRequiredService<ConditionSet>();
        var engine = (BackgroundService)provider.GetServices<IHostedService>().Single();

        // The refresher is not running: read the (empty) control state once, so Homeostat does not count as paused.
        await provider.GetRequiredService<SharedControl>().TryRefreshAsync(cancellationToken).ConfigureAwait(false);
        using var processed = new SemaphoreSlim(0);
        channel.ProcessedNotifier = processed;

        var last = first.Timestamp;
        var count = 0;
        await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recorded = signals.Current;
                if (recorded.Timestamp > time.GetUtcNow())
                {
                    time.SetUtcNow(recorded.Timestamp);
                }

                if (recorded.Timestamp > last)
                {
                    last = recorded.Timestamp;
                }

                if (!channel.TryPublish(recorded.ToSignal()))
                {
                    break;
                }

                count++;

                // Wait for the engine to process the signal, failing fast if the engine has stopped.
                var wait = processed.WaitAsync(cancellationToken);
                var executing = engine.ExecuteTask ?? Task.CompletedTask;
                if (await Task.WhenAny(wait, executing).ConfigureAwait(false) != wait)
                {
                    await executing.ConfigureAwait(false);
                    throw new InvalidOperationException("The Homeostat engine stopped during the replay.");
                }

                await wait.ConfigureAwait(false);
                await runner.WhenIdleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            while (signals.MoveNext());
        }
        finally
        {
            await engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var plans = conditions.Trackers
            .Where(t => t.Definition.Plan is not null)
            .ToDictionary<ConditionTracker, string, IReadOnlyList<string>>(t => t.Definition.Name, t => [.. t.Definition.Plan!.Steps.Select(s => s.Name)], StringComparer.Ordinal);
        var summaries = conditions.Trackers.Select(t => t.Definition.Name).Select(name => new ReplayConditionSummary(
            name,
            recorder.Events.Count(e => e.Condition == name && e.Kind == ReplayEventKind.Activated),
            recorder.Events.Count(e => e.Condition == name && e.Kind == ReplayEventKind.WouldHeal),
            plans.GetValueOrDefault(name) ?? [])).ToList();

        return new ReplayReport(first.Timestamp, last, count, recorder.Events, summaries, recorder.Runs);
    }

    private sealed class ReplayRecorder(TimeProvider time) : IHealingObserver
    {
        private readonly object _gate = new();
        private readonly List<ReplayEvent> _events = [];
        private readonly List<HealingRunResult> _runs = [];

        public IReadOnlyList<ReplayEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public IReadOnlyList<HealingRunResult> Runs
        {
            get
            {
                lock (_gate)
                {
                    return [.. _runs];
                }
            }
        }

        public void OnConditionActivated(ConditionSnapshot condition)
            => Add(new ReplayEvent(time.GetUtcNow(), condition.Name, ReplayEventKind.Activated, condition.LastSignal?.Message));

        public void OnConditionCleared(ConditionSnapshot condition)
            => Add(new ReplayEvent(time.GetUtcNow(), condition.Name, ReplayEventKind.Cleared, null));

        public void OnStepCompleted(HealingStepResult step)
        {
        }

        public void OnRunCompleted(HealingRunResult run)
        {
            var kind = run.Outcome == HealingRunOutcome.DryRun ? ReplayEventKind.WouldHeal : ReplayEventKind.Blocked;
            lock (_gate)
            {
                _runs.Add(run);
            }

            Add(new ReplayEvent(run.StartedAt, run.ConditionName, kind, run.Outcome.ToString()));
        }

        private void Add(ReplayEvent e)
        {
            lock (_gate)
            {
                _events.Add(e);
            }
        }
    }
}

/// <summary>Kinds of <see cref="ReplayEvent"/>.</summary>
public enum ReplayEventKind
{
    /// <summary>The condition became active.</summary>
    Activated,

    /// <summary>The condition cleared.</summary>
    Cleared,

    /// <summary>The plan would have run (dry-run).</summary>
    WouldHeal,

    /// <summary>
    /// A run started but ended without acting (for example paused, over budget, or suppressed by another condition). A
    /// condition still in its cooldown starts no run, so it produces no event.
    /// </summary>
    Blocked,
}

/// <summary>Something that happened during a replay.</summary>
/// <param name="At">Signal time when it happened.</param>
/// <param name="Condition">Condition concerned.</param>
/// <param name="Kind">What happened.</param>
/// <param name="Detail">Latest signal message, or the run outcome.</param>
public sealed record ReplayEvent(DateTimeOffset At, string Condition, ReplayEventKind Kind, string? Detail);

/// <summary>What a condition did during a replay.</summary>
/// <param name="Condition">Condition name.</param>
/// <param name="Activations">Times it became active.</param>
/// <param name="WouldHeal">Times its plan would have run.</param>
/// <param name="PlanSteps">Steps of its plan, in order.</param>
public sealed record ReplayConditionSummary(string Condition, int Activations, int WouldHeal, IReadOnlyList<string> PlanSteps);

/// <summary>Result of <see cref="HomeostatReplay"/>.</summary>
/// <param name="From">First signal time.</param>
/// <param name="To">Last signal time.</param>
/// <param name="Signals">Signals replayed.</param>
/// <param name="Events">Activations, clears and runs, in order.</param>
/// <param name="Conditions">One summary per condition.</param>
/// <param name="Runs">The dry runs, with their outcomes.</param>
public sealed record ReplayReport(
    DateTimeOffset From,
    DateTimeOffset To,
    int Signals,
    IReadOnlyList<ReplayEvent> Events,
    IReadOnlyList<ReplayConditionSummary> Conditions,
    IReadOnlyList<HealingRunResult> Runs)
{
    /// <summary>A readable report: one line per condition, then the timeline.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Replay of {Signals} signals from {From:u} to {To:u}");
        foreach (var c in Conditions)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {c.Condition}: active {c.Activations} times, would heal {c.WouldHeal} times{(c.PlanSteps.Count > 0 ? $" ({string.Join(" -> ", c.PlanSteps)})" : string.Empty)}");
        }

        if (Events.Count > 0)
        {
            text.AppendLine("Timeline:");
            foreach (var e in Events)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {e.At:u}  {e.Condition}  {e.Kind}{(e.Detail is null ? string.Empty : $"  {e.Detail}")}");
            }
        }

        return text.ToString();
    }
}
