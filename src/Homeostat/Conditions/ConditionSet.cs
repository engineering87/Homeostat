namespace Homeostat.Conditions;

/// <summary>Holds a tracker for every registered condition, validated as a whole.</summary>
internal sealed class ConditionSet
{
    // A pending cause without any signal for this long (or for its SustainedFor, if longer) no longer suppresses: its
    // source stopped reporting, and it would otherwise hold its symptoms back for ever.

    private readonly TimeProvider _timeProvider;
    private volatile State _state;

    public ConditionSet(IEnumerable<ConditionDefinition> definitions, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _state = Build(definitions.ToList(), existing: null);
    }

    /// <summary>All trackers, composite ones last, in dependency order.</summary>
    public IReadOnlyList<ConditionTracker> Trackers => _state.Trackers;

    /// <summary>Trackers driven by signals.</summary>
    public IReadOnlyList<ConditionTracker> SignalTrackers => _state.SignalTrackers;

    /// <summary>
    /// The signal trackers that can be interested in a signal from <paramref name="source"/>, in registration order:
    /// those listening to that source and those without a source.
    /// </summary>
    public ConditionTracker[] SignalTrackersFor(string? source)
    {
        var state = _state;
        return source is not null && state.BySource.TryGetValue(source, out var trackers) ? trackers : state.Unsourced;
    }

    /// <summary>Composite trackers, each after the composites it depends on.</summary>
    public ConditionTracker[] CompositeTrackers => _state.CompositeTrackers;

    public ConditionTracker? Get(string name) => _state.ByName.GetValueOrDefault(name);

    /// <summary>
    /// Replaces the definitions, keeping the state of conditions whose name did not change. Throws, leaving the
    /// current set untouched, if the new definitions are invalid.
    /// </summary>
    public void Replace(IEnumerable<ConditionDefinition> definitions)
    {
        var updates = new List<(ConditionTracker Tracker, ConditionDefinition Definition)>();
        _state = Build(definitions.ToList(), _state, updates);

        // Kept trackers take their new definition once the new source index is in place, so a condition whose source
        // changed is offered the signals of its new source from the moment it evaluates them.
        foreach (var (tracker, definition) in updates)
        {
            tracker.Update(definition);
        }
    }

    /// <summary>
    /// Returns the first condition listed in <see cref="ConditionDefinition.SuppressedBy"/> that is pending or active.
    /// A pending cause counts, so a symptom with a shorter <c>SustainedFor</c> does not act before its cause activates.
    /// </summary>
    public string? FindActiveSuppressor(ConditionDefinition definition)
    {
        var byName = _state.ByName;
        foreach (var name in definition.SuppressedBy)
        {
            if (!byName.TryGetValue(name, out var cause))
            {
                continue;
            }

            var status = cause.Status;
            if (status == ConditionStatus.Active)
            {
                return name;
            }

            if (status == ConditionStatus.Pending && cause.LastTriggeredAt is { } last)
            {
                if (_timeProvider.GetUtcNow() - last < ConditionTracker.StaleAfter(cause.Definition))
                {
                    return name;
                }
            }
        }

        return null;
    }

    /// <summary>Whether a composite condition's combination currently holds.</summary>
    public bool CompositeHolds(ConditionDefinition composite)
    {
        // A loop, not LINQ: this runs for every signal and every composite.
        var byName = _state.ByName;
        var all = composite.Composite == CompositeMode.All;
        foreach (var name in composite.DependsOn)
        {
            var active = byName.TryGetValue(name, out var tracker) && tracker.Status == ConditionStatus.Active;
            if (active != all)
            {
                return active;
            }
        }

        return all;
    }

    /// <summary>Snapshot that also reports the active suppressor.</summary>
    public ConditionSnapshot Snapshot(ConditionTracker tracker)
        => tracker.Snapshot() with { SuppressedBy = FindActiveSuppressor(tracker.Definition) };

    private static State Build(
        List<ConditionDefinition> definitions,
        State? existing,
        List<(ConditionTracker Tracker, ConditionDefinition Definition)>? updates = null)
    {
        var duplicate = definitions.GroupBy(d => d.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"The condition '{duplicate.Key}' is registered more than once.");
        }

        var definitionsByName = definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);
        foreach (var definition in definitions)
        {
            CheckReferences(definition, definition.SuppressedBy, "is suppressed by", definitionsByName);
            CheckReferences(definition, definition.DependsOn, "depends on", definitionsByName);
        }

        // A suppression cycle would let two active conditions block each other forever; a composite cycle has no meaning.
        CheckCycles(definitionsByName, d => d.SuppressedBy, "Suppression");
        var compositeOrder = CheckCycles(definitionsByName, d => d.DependsOn, "Composite condition");

        var trackers = definitions.Select(d =>
        {
            if (existing?.ByName.GetValueOrDefault(d.Name) is { } kept)
            {
                // A changed condition gets a new definition: its seasonal baseline keeps what it learned when the cycle
                // is the same (same key), instead of waiting for the keeper to reload a copy up to minutes old.
                foreach (var baseline in d.Baselines)
                {
                    if (kept.Definition.Baselines.FirstOrDefault(b => b.Key == baseline.Key) is { } previous && !ReferenceEquals(previous, baseline))
                    {
                        baseline.Import(previous.Export());
                        baseline.Loaded = previous.Loaded;
                    }
                }

                updates!.Add((kept, d));
                return kept;
            }

            return new ConditionTracker(d);
        }).ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);

        var signalTrackers = definitions.Where(d => d.Composite is null).Select(d => trackers[d.Name]).ToArray();
        var compositeTrackers = compositeOrder
            .Where(name => definitionsByName[name].Composite is not null)
            .Select(name => trackers[name])
            .ToArray();

        // Most conditions listen to one source: index them, so a signal is only offered to the conditions that can
        // use it. Conditions without a source (a custom filter only) are offered every signal, in registration order.
        // Kept trackers still hold their previous definition until the caller applies the updates: use the new one.
        string? SourceOf(ConditionTracker t) => definitionsByName[t.Definition.Name].Source;

        var unsourced = signalTrackers.Where(t => SourceOf(t) is null).ToArray();
        var bySource = signalTrackers
            .Where(t => SourceOf(t) is not null)
            .Select(t => SourceOf(t)!)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(
                source => source,
                source => signalTrackers.Where(t => SourceOf(t) is null || string.Equals(SourceOf(t), source, StringComparison.Ordinal)).ToArray(),
                StringComparer.Ordinal);

        return new State(
            signalTrackers.Concat(compositeTrackers).ToArray(),
            signalTrackers,
            compositeTrackers,
            trackers,
            bySource,
            unsourced);
    }

    private static void CheckReferences(
        ConditionDefinition definition,
        IReadOnlyList<string> references,
        string relation,
        Dictionary<string, ConditionDefinition> byName)
    {
        foreach (var reference in references)
        {
            if (string.Equals(reference, definition.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The condition '{definition.Name}' {relation} itself.");
            }

            if (!byName.ContainsKey(reference))
            {
                throw new InvalidOperationException($"The condition '{definition.Name}' {relation} '{reference}', which is not registered.");
            }
        }
    }

    /// <summary>Throws on a cycle; returns the names in dependency order (dependencies first).</summary>
    private static List<string> CheckCycles(
        Dictionary<string, ConditionDefinition> byName,
        Func<ConditionDefinition, IReadOnlyList<string>> edges,
        string kind)
    {
        var order = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done

        foreach (var name in byName.Keys)
        {
            Visit(name, []);
        }

        return order;

        void Visit(string name, List<string> path)
        {
            if (state.TryGetValue(name, out var s))
            {
                if (s == 1)
                {
                    var cycle = path.Skip(path.IndexOf(name)).Append(name);
                    throw new InvalidOperationException($"{kind} cycle between conditions: {string.Join(" -> ", cycle)}.");
                }

                return;
            }

            state[name] = 1;
            path.Add(name);
            foreach (var next in edges(byName[name]))
            {
                Visit(next, path);
            }

            path.RemoveAt(path.Count - 1);
            state[name] = 2;
            order.Add(name);
        }
    }

    private sealed record State(
        ConditionTracker[] Trackers,
        ConditionTracker[] SignalTrackers,
        ConditionTracker[] CompositeTrackers,
        Dictionary<string, ConditionTracker> ByName,
        Dictionary<string, ConditionTracker[]> BySource,
        ConditionTracker[] Unsourced);
}
