using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Homeostat.Signals;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Recording;

/// <summary>
/// One recorded signal, as written to the recording files (JSON Lines) and read back by replays.
/// </summary>
/// <param name="Timestamp">Signal time.</param>
/// <param name="Source">Signal source.</param>
/// <param name="Severity">Signal severity.</param>
/// <param name="Value">Signal value, if any.</param>
/// <param name="Message">Signal message, if recorded.</param>
/// <param name="Tags">Signal tags, if recorded.</param>
/// <param name="Instance">Instance that recorded the signal (<see cref="HomeostatOptions.InstanceId"/>).</param>
public sealed record RecordedSignal(
    DateTimeOffset Timestamp,
    string Source,
    SignalSeverity Severity,
    double? Value = null,
    string? Message = null,
    IReadOnlyDictionary<string, string>? Tags = null,
    string? Instance = null)
{
    internal static readonly JsonSerializerOptions Json = HomeostatJsonContext.CreateOptions(o =>
    {
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        o.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    });

    /// <summary>The signal to publish again.</summary>
    public Signal ToSignal() => new(Source, Severity, Timestamp) { Value = Value, Message = Message, Tags = Tags };

    /// <summary>Reads a recording file, skipping lines that are not valid recorded signals.</summary>
    /// <param name="path">A file written by <c>RecordSignals</c>.</param>
    public static IEnumerable<RecordedSignal> Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            RecordedSignal? signal;
            try
            {
                signal = JsonSerializer.Deserialize(line, Json.TypeInfo<RecordedSignal>());
            }
            catch (JsonException)
            {
                continue; // A line cut by a crash, for example.
            }

            if (signal is { Source.Length: > 0 })
            {
                yield return signal;
            }
        }
    }
}

/// <summary>
/// Writes the signals the engine receives to daily JSON Lines files, in the background. It keeps writing until every
/// other service has stopped (<see cref="StoppedAsync"/>), so the signals the engine processes while stopping are kept.
/// </summary>
internal sealed class SignalRecorder : IHostedLifecycleService
{
    private Task _loop = Task.CompletedTask;

    private readonly Channel<Signal> _queue;
    private readonly SignalRecordingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SignalRecorder> _logger;
    private readonly string _instance;
    private readonly string _path;
    private string? _lastCleanupDate;

    public SignalRecorder(
        IOptions<SignalRecordingOptions> options, IOptions<HomeostatOptions> homeostat, TimeProvider timeProvider, ILogger<SignalRecorder> logger)
    {
        _options = options.Value;
        _instance = homeostat.Value.InstanceId;
        var safe = string.Concat(_instance.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
        _path = _options.Path.Replace("{instance}", safe, StringComparison.Ordinal);
        _timeProvider = timeProvider;
        _logger = logger;
        _queue = Channel.CreateBounded<Signal>(new BoundedChannelOptions(_options.BufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
    }

    /// <summary>Queues a signal without blocking the engine.</summary>
    public void Record(Signal signal)
    {
        if (signal.Source.StartsWith("homeostat:composite:", StringComparison.Ordinal)
            || (_options.Sources.Count > 0 && !_options.Sources.Contains(signal.Source)))
        {
            return;
        }

        _queue.Writer.TryWrite(signal);
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_path.Contains("{date}", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "The signal recording path {Path} has no {{date}}: everything goes to one file, which retention never deletes.", _path);
        }

        _loop = Task.Run(WriteLoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Keep recording while the other services stop: the engine processes its last signals then.
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Every other service has stopped: write what is queued, within the shutdown timeout.</summary>
    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        var batch = new List<Signal>();
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < 500 && _queue.Reader.TryRead(out var signal))
            {
                batch.Add(signal);
            }

            try
            {
                await WriteAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Recording is best effort: a failure must never stop the loop (and so the draining at shutdown).
                _logger.LogWarning(ex, "Recording {Count} signals failed; they are dropped.", batch.Count);
            }
        }
    }

    internal string PathFor(DateTimeOffset at)
        => _path.Replace("{date}", at.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private async Task WriteAsync(List<Signal> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var group in batch.GroupBy(s => PathFor(s.Timestamp)))
            {
                var text = new StringBuilder();
                foreach (var signal in group)
                {
                    var recorded = new RecordedSignal(signal.Timestamp, signal.Source, signal.Severity, signal.Value,
                        _options.IncludeDetails ? signal.Message : null, _options.IncludeDetails ? signal.Tags : null, _instance);
                    text.Append(JsonSerializer.Serialize(recorded, RecordedSignal.Json.TypeInfo<RecordedSignal>())).Append('\n');
                }

                if (System.IO.Path.GetDirectoryName(group.Key) is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                }

                await AppendAsync(group.Key, text.ToString(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Recording {Count} signals failed; they are dropped.", batch.Count);
        }

        Cleanup();
    }

    /// <summary>Appends to a file that, on Unix, is created readable and writable by the owner only.</summary>
    private static async Task AppendAsync(string path, string text, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var stream = new FileStream(path, options);
        await using (stream.ConfigureAwait(false))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Deletes daily files older than the retention, once a day.</summary>
    private void Cleanup()
    {
        var today = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (_options.RetentionDays <= 0 || !_path.Contains("{date}", StringComparison.Ordinal) || _lastCleanupDate == today)
        {
            return;
        }

        _lastCleanupDate = today;
        var template = System.IO.Path.GetFullPath(_path);
        var directory = System.IO.Path.GetDirectoryName(template)!;
        var name = System.IO.Path.GetFileName(template);
        var at = name.IndexOf("{date}", StringComparison.Ordinal);
        if (at < 0 || !Directory.Exists(directory))
        {
            return; // {date} in a directory name: not cleaned up.
        }

        var (prefix, suffix) = (name[..at], name[(at + "{date}".Length)..]);
        var oldest = _timeProvider.GetUtcNow().UtcDateTime.Date.AddDays(-_options.RetentionDays);
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, prefix + "*" + suffix).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Listing the signal recordings in {Directory} failed; retention is applied next time.", directory);
            return;
        }

        foreach (var file in files)
        {
            // Only this recorder's own daily files: the exact name with a date, nothing longer or shorter.
            var fileName = System.IO.Path.GetFileName(file);
            var datePart = fileName.Length == prefix.Length + 10 + suffix.Length
                && fileName.StartsWith(prefix, StringComparison.Ordinal) && fileName.EndsWith(suffix, StringComparison.Ordinal)
                ? fileName.Substring(prefix.Length, 10)
                : string.Empty;
            if (DateTime.TryParseExact(datePart, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var date)
                && date < oldest)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Deleting the old signal recording {File} failed; retried tomorrow.", file);
                }
            }
        }
    }
}
