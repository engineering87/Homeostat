using System.Text.Json;
using System.Text.Json.Serialization;

namespace Homeostat.Persistence;

/// <summary>
/// Store for a single instance that must remember its state across restarts: keeps everything in memory and
/// writes a JSON file after every change (atomically, through a temporary file flushed to disk). A file that cannot
/// be read is renamed to <c>*.corrupt</c> and the store starts empty, so a damaged file never prevents startup.
/// </summary>
public sealed class FileHomeostatStore : InMemoryHomeostatStore
{
    private static readonly JsonSerializerOptions Json = HomeostatJsonContext.CreateOptions(o => o.WriteIndented = true);

    private readonly string _path;

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
    public FileHomeostatStore(string path, FileStoreOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PlanStateRetention, TimeSpan.Zero, nameof(options));
        _path = System.IO.Path.GetFullPath(path);

        if (File.Exists(_path))
        {
            Document? document;
            try
            {
                using var stream = File.OpenRead(_path);
                document = JsonSerializer.Deserialize(stream, Json.TypeInfo<Document>());
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                File.Move(_path, _path + ".corrupt", overwrite: true);
                document = null;
            }

            if (document is not null)
            {
                // Forget plans that have not run for a long time (renamed conditions, old instance names).
                var cutoff = DateTimeOffset.UtcNow - options.PlanStateRetention;
                var plans = (document.Plans ?? [])
                    .Where(p => p.Value.Held || p.Value.LastRunEndedAt is null || p.Value.LastRunEndedAt >= cutoff)
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                Restore(new StoreSnapshot(plans, document.Budget ?? [], document.Runs ?? [])
                {
                    Events = document.Conditions ?? [],
                    Documents = document.Documents ?? [],
                });
            }
        }
    }

    /// <summary>Full path of the file.</summary>
    public string Path => _path;

    /// <inheritdoc />
    private protected override void OnChanged()
    {
        var snapshot = Snapshot();
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
    }
}
