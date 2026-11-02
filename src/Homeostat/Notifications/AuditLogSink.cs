using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Notifications;

/// <summary>Appends every event as one JSON object per line.</summary>
internal sealed class AuditLogSink : HomeostatEventSink
{
    internal static readonly JsonSerializerOptions Json = HomeostatJsonContext.CreateOptions(o =>
    {
        o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        // Keeps quotes and non-ASCII text readable; the output is JSON for files and APIs, never embedded in HTML.
        o.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    });

    private readonly string _pathTemplate;

    public AuditLogSink(
        IOptions<AuditLogOptions> audit,
        IOptions<HomeostatOptions> options,
        TimeProvider timeProvider,
        ILogger<AuditLogSink> logger)
        : base(options.Value.ApplicationName, audit.Value.Events, timeProvider, logger)
    {
        _pathTemplate = audit.Value.Path;
    }

    protected override async Task WriteAsync(HomeostatEvent homeostatEvent, CancellationToken cancellationToken)
    {
        var path = System.IO.Path.GetFullPath(
            _pathTemplate.Replace("{date}", homeostatEvent.Timestamp.UtcDateTime.ToString("yyyy-MM-dd"), StringComparison.Ordinal));

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(homeostatEvent, Json.TypeInfo<HomeostatEvent>()) + "\n";

        // Open per event: the volume is low (a few events per incident), and other processes can rotate or read the file.
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
    }
}
