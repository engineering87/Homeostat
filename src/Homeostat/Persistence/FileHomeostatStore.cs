using System.Text.Json;
using Homeostat.Healing;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Homeostat.Persistence;

/// <summary>
/// Store for a single instance that must remember its state across restarts: keeps everything in memory and
/// writes a JSON file after every change (atomically, through a temporary file flushed to disk). A file that cannot
/// be read is renamed to <c>*.corrupt</c> and the store starts empty but paused (the file may have held a person's
/// pause), so a damaged file never prevents startup and Homeostat does not act until a person resumes it. While
/// the store is open it holds an exclusive lock on <c>{path}.lock</c>: a second process opening the same file fails
/// at once instead of overwriting the other's state.
/// </summary>
/// <remarks>
/// The pause, the pauses of healing groups, the action budgets and the switches turned on by a person or by code are
/// kept across restarts; switches turned on by a plan are not, since the condition that owns them starts again from
/// scratch. Approval requests are not kept: the run that waits for them does not survive a restart.
/// </remarks>
public sealed class FileHomeostatStore : IHomeostatStore, IDisposable
{
    private static readonly JsonSerializerOptions Json = HomeostatJsonContext.CreateOptions(o => o.WriteIndented = true);

    private readonly string _path;
    private readonly PersistentStore _store;
    private readonly FileStream _lock;

    /// <summary>Opens the store, loading the file if it exists, with the default <see cref="FileStoreOptions"/>.</summary>
    /// <param name="path">File path; the directory is created if needed.</param>
    public FileHomeostatStore(string path)
        : this(path, new FileStoreOptions())
    {
    }

    /// <summary>Opens the store, loading the file if it exists.</summary>
    /// <param name="path">File path; the directory is created if needed.</param>
    /// <param name="options">Retention settings.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="FileStoreOptions.PlanStateRetention"/> is not positive.</exception>
    /// <exception cref="IOException">Another process (or another store in this one) has the file open.</exception>
    public FileHomeostatStore(string path, FileStoreOptions options)
        : this(path, options, logger: null)
    {
    }

    /// <summary>Opens the store, logging a file that cannot be read.</summary>
    internal FileHomeostatStore(string path, FileStoreOptions options, ILogger? logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PlanStateRetention, TimeSpan.Zero, nameof(options));
        _path = System.IO.Path.GetFullPath(path);
        _store = new PersistentStore(this);
        _lock = OpenLock(_path);

        try
        {
            Document? document = null;
            Exception? error = null;
            var unreadable = File.Exists(_path) && !TryLoad(_path, out document, out error);
            if (unreadable)
            {
                // The file may have held a person's pause: starting empty would let Homeostat act through it. Start
                // paused instead, until a person resumes on purpose. Written before the file is set aside: if it cannot
                // be written, the unreadable file stays and the store does not open (tried again later), rather than
                // opening without the pause.
                var at = DateTimeOffset.UtcNow;
                var pause = new PauseState { At = at, By = $"store file unreadable (set aside as {System.IO.Path.GetFileName(_path)}.corrupt at {at:O})" };
                _store.Restore(new MemoryStore.StoreSnapshot(new Dictionary<string, PlanState>(StringComparer.Ordinal), [], []) { Pause = pause });
                WriteTemporary(_store.Snapshot());
                File.Move(_path, _path + ".corrupt", overwrite: true);
                File.Move(_path + ".tmp", _path, overwrite: true);
                (logger ?? NullLogger.Instance).LogCritical(
                    error,
                    "The Homeostat store file {Path} could not be read: it was set aside as {Corrupt} and Homeostat is paused until a person resumes it.",
                    _path, _path + ".corrupt");
            }
            else if (document is not null)
            {
                // Forget plans that have not run for a long time (renamed conditions, old instance names).
                var cutoff = DateTimeOffset.UtcNow - options.PlanStateRetention;
                var groupPauses = LoadGroupPauses(document.GroupPauses, logger);
                var plans = (document.Plans ?? [])
                    .Where(p => p.Value is not null && (p.Value.Held || p.Value.LastRunEndedAt is null || p.Value.LastRunEndedAt >= cutoff))
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                _store.Restore(new MemoryStore.StoreSnapshot(plans, document.Budget ?? [], WithoutNulls(document.Runs))
                {
                    Events = WithoutNulls(document.Conditions),
                    Documents = (document.Documents ?? []).Where(d => d.Value is not null).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal),
                    Pause = document.Pause,
                    Switches = WithoutNulls(document.Switches).Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToArray(),
                    GroupPauses = groupPauses,
                    GroupBudgets = (document.GroupBudgets ?? [])
                        .Where(b => !string.IsNullOrWhiteSpace(b.Key) && b.Value is not null)
                        .ToDictionary(b => b.Key, b => (IReadOnlyList<DateTimeOffset>)b.Value, StringComparer.Ordinal),
                    GroupBudgetEnds = (document.GroupBudgetEnds ?? [])
                        .Where(b => !string.IsNullOrWhiteSpace(b.Key))
                        .ToDictionary(b => b.Key, b => b.Value, StringComparer.Ordinal),
                });
            }

            _store.Loaded();
        }
        catch
        {
            _lock.Dispose();
            throw;
        }
    }

    /// <summary>Full path of the file.</summary>
    public string Path => _path;

    /// <summary>Releases the lock on the file; the state was already written.</summary>
    public void Dispose() => _lock.Dispose();

    private static FileStream OpenLock(string path)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new IOException(
                $"The Homeostat store file '{path}' is in use by another process: the file store is for one instance. "
                + "Give each instance its own file, or use a shared store such as Homeostat.Redis.", ex);
        }
    }

    // Anything that cannot be read (bad JSON, a document that is only null, a hand edit with nulls where records belong)
    // is set aside by the caller, never fatal.
    private static bool TryLoad(string path, out Document? document, out Exception? error)
    {
        error = null;
        try
        {
            using var stream = File.OpenRead(path);
            document = JsonSerializer.Deserialize(stream, Json.TypeInfo<Document>());
            return document is not null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            error = ex;
            document = null;
            return false;
        }
    }

    // An unreadable pause counts as a pause (decision S8): no pause of the file is dropped. A group pause without a group
    // cannot be applied to any group: like a switch without a name, left out. Two pauses of one group (a hand edit): the
    // later decision is kept, as the shared view keeps it. More than ControlState.MaxGroupPauses (a hand edit): all are
    // kept, since dropping one would let Homeostat act through it; the limit only refuses new pauses until groups are
    // resumed (memory stays bounded by the file, which the store never makes longer than the limit).
    private static GroupPauseState[] LoadGroupPauses(List<GroupPauseState>? stored, ILogger? logger)
    {
        var pauses = WithoutNulls(stored).Where(p => !string.IsNullOrWhiteSpace(p.Group))
            .GroupBy(p => p.Group, StringComparer.Ordinal)
            .Select(g => g.MaxBy(p => p.At)!)
            .ToArray();
        if (pauses.Length > ControlState.MaxGroupPauses)
        {
            (logger ?? NullLogger.Instance).LogCritical(
                "The Homeostat store file holds {Count} group pauses, more than the {Limit} the store allows: all are kept, and no other group can be paused until groups are resumed.",
                pauses.Length, ControlState.MaxGroupPauses);
        }

        return pauses;
    }

    private static T[] WithoutNulls<T>(List<T>? items)
        where T : class
        => items is null ? [] : items.Where(i => i is not null).ToArray();

    private void Save(MemoryStore.StoreSnapshot snapshot)
    {
        WriteTemporary(snapshot);
        File.Move(_path + ".tmp", _path, overwrite: true);
    }

    // Writes the state to {path}.tmp, flushed to disk; the caller moves it into place.
    private void WriteTemporary(MemoryStore.StoreSnapshot snapshot)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(
                stream,
                new Document(new Dictionary<string, PlanState>(snapshot.Plans), snapshot.Budget.ToList(), snapshot.Runs.ToList())
                {
                    Conditions = snapshot.Events.ToList(),
                    Documents = new Dictionary<string, string>(snapshot.Documents, StringComparer.Ordinal),
                    Pause = snapshot.Pause,
                    Switches = snapshot.Switches.Where(s => s.Condition is null).ToList(),
                    GroupPauses = snapshot.GroupPauses.Count == 0 ? null : snapshot.GroupPauses.OrderBy(p => p.Group, StringComparer.Ordinal).ToList(),
                    GroupBudgets = snapshot.GroupBudgets.Count == 0
                        ? null
                        : snapshot.GroupBudgets.ToDictionary(b => b.Key, b => b.Value.ToList(), StringComparer.Ordinal),
                    GroupBudgetEnds = snapshot.GroupBudgetEnds.Count == 0
                        ? null
                        : snapshot.GroupBudgetEnds.ToDictionary(b => b.Key, b => b.Value, StringComparer.Ordinal),
                },
                Json.TypeInfo<Document>());
            stream.Flush(flushToDisk: true);
        }
    }

    internal sealed record Document(
        Dictionary<string, PlanState>? Plans,
        List<DateTimeOffset>? Budget,
        List<HealingRunRecord>? Runs)
    {
        public List<ConditionEventRecord>? Conditions { get; init; }

        public Dictionary<string, string>? Documents { get; init; }

        public PauseState? Pause { get; init; }

        public List<SwitchState>? Switches { get; init; }

        public List<GroupPauseState>? GroupPauses { get; init; }

        public Dictionary<string, List<DateTimeOffset>>? GroupBudgets { get; init; }

        /// <summary>When each group budget is empty (its latest unit has left its window): removed from then on.</summary>
        public Dictionary<string, DateTimeOffset>? GroupBudgetEnds { get; init; }
    }

    /// <summary>The in-memory state, written to the file after every change.</summary>
    private sealed class PersistentStore(FileHomeostatStore owner) : MemoryStore
    {
        // What the file holds: a change that cannot be written is undone, so memory never runs ahead of the file (a
        // retried write would otherwise be applied twice, and a refused budget unit would stay spent).
        private StoreSnapshot? _saved;

        public void Loaded() => _saved = Snapshot();

        protected override void OnChanged()
        {
            var snapshot = Snapshot();
            try
            {
                owner.Save(snapshot);
                _saved = snapshot;
            }
            catch
            {
                if (_saved is { } saved)
                {
                    Restore(saved);
                }

                throw;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => _store.GetPlanStateAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) => _store.UpdatePlanStateAsync(key, update, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _store.TryConsumeBudgetAsync(now, limit, window, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => _store.GetBudgetUsedAsync(now, window, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryConsumeGroupBudgetAsync(string group, DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _store.TryConsumeGroupBudgetAsync(group, now, limit, window, cancellationToken);

    /// <inheritdoc />
    public ValueTask<int> GetGroupBudgetUsedAsync(string group, DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => _store.GetGroupBudgetUsedAsync(group, now, window, cancellationToken);

    /// <inheritdoc />
    public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => _store.AppendRunAsync(run, maxRuns, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) => _store.GetRecentRunsAsync(maxRuns, cancellationToken);

    /// <inheritdoc />
    public ValueTask AppendConditionEventAsync(ConditionEventRecord conditionEvent, int maxEvents, CancellationToken cancellationToken = default) => _store.AppendConditionEventAsync(conditionEvent, maxEvents, cancellationToken);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ConditionEventRecord>> GetConditionEventsAsync(int maxEvents, CancellationToken cancellationToken = default) => _store.GetConditionEventsAsync(maxEvents, cancellationToken);

    /// <inheritdoc />
    public ValueTask<string?> GetDocumentAsync(string key, CancellationToken cancellationToken = default) => _store.GetDocumentAsync(key, cancellationToken);

    /// <inheritdoc />
    public ValueTask SetDocumentAsync(string key, string document, CancellationToken cancellationToken = default) => _store.SetDocumentAsync(key, document, cancellationToken);

    /// <inheritdoc />
    public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) => _store.GetControlStateAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _store.TrySetPauseAsync(pause, decidedAt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TrySetGroupPauseAsync(string group, PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _store.TrySetGroupPauseAsync(group, pause, decidedAt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _store.TrySetSwitchAsync(name, state, decidedAt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _store.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) => _store.TryClaimSwitchAsync(state, cancellationToken);

    /// <inheritdoc />
    public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) => _store.AddApprovalAsync(approval, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default) => _store.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken);

    /// <inheritdoc />
    public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) => _store.RemoveApprovalAsync(id, cancellationToken);

    /// <inheritdoc />
    public bool SupportsConditionHistory => true;

    /// <inheritdoc />
    public bool SupportsDocuments => true;
}
