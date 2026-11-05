using System.Text.Json;
using Homeostat.Healing;
using System.Text.Json.Serialization;

namespace Homeostat.Persistence;

/// <summary>
/// Store for a single instance that must remember its state across restarts: keeps everything in memory and
/// writes a JSON file after every change (atomically, through a temporary file flushed to disk). A file that cannot
/// be read is renamed to <c>*.corrupt</c> and the store starts empty, so a damaged file never prevents startup. While
/// the store is open it holds an exclusive lock on <c>{path}.lock</c>: a second process opening the same file fails
/// at once instead of overwriting the other's state.
/// </summary>
/// <remarks>
/// The pause and the switches turned on by a person or by code are kept across restarts; switches turned on by a plan
/// are not, since the condition that owns them starts again from scratch. Approval requests are not kept: the run that
/// waits for them does not survive a restart.
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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PlanStateRetention, TimeSpan.Zero, nameof(options));
        _path = System.IO.Path.GetFullPath(path);
        _store = new PersistentStore(this);
        _lock = OpenLock(_path);

        try
        {
            if (File.Exists(_path) && Load(_path) is { } document)
            {
                // Forget plans that have not run for a long time (renamed conditions, old instance names).
                var cutoff = DateTimeOffset.UtcNow - options.PlanStateRetention;
                var plans = (document.Plans ?? [])
                    .Where(p => p.Value is not null && (p.Value.Held || p.Value.LastRunEndedAt is null || p.Value.LastRunEndedAt >= cutoff))
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                _store.Restore(new MemoryStore.StoreSnapshot(plans, document.Budget ?? [], WithoutNulls(document.Runs))
                {
                    Events = WithoutNulls(document.Conditions),
                    Documents = (document.Documents ?? []).Where(d => d.Value is not null).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal),
                    Pause = document.Pause,
                    Switches = WithoutNulls(document.Switches).Where(s => !string.IsNullOrWhiteSpace(s.Name)).ToArray(),
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

    // Anything that cannot be read (bad JSON, a hand edit with nulls where records belong) is set aside, never fatal.
    private static Document? Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, Json.TypeInfo<Document>());
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            File.Move(path, path + ".corrupt", overwrite: true);
            return null;
        }
    }

    private static T[] WithoutNulls<T>(List<T>? items)
        where T : class
        => items is null ? [] : items.Where(i => i is not null).ToArray();

    private void Save(MemoryStore.StoreSnapshot snapshot)
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
                },
                Json.TypeInfo<Document>());
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, _path, overwrite: true);
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
    public ValueTask SetPauseAsync(PauseState? pause, CancellationToken cancellationToken = default) => _store.SetPauseAsync(pause, cancellationToken);

    /// <inheritdoc />
    public ValueTask SetSwitchAsync(string name, SwitchState? state, CancellationToken cancellationToken = default) => _store.SetSwitchAsync(name, state, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _store.TrySetPauseAsync(pause, decidedAt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _store.TrySetSwitchAsync(name, state, decidedAt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _store.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) => _store.TryClaimSwitchAsync(state, cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, CancellationToken cancellationToken = default)
        => _store.TryReleaseSwitchAsync(name, condition, instance, cancellationToken);

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
