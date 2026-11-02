using Homeostat.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat;

/// <summary>Registers the built-in audit log.</summary>
public static class HomeostatAuditLogExtensions
{
    /// <summary>
    /// Records every condition, step, run and approval event as one JSON object per line in <paramref name="path"/>.
    /// Writing happens in the background and never slows down healing.
    /// </summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="path">File path; <c>{date}</c> is replaced with the UTC date to get one file per day.</param>
    /// <example><code>.AddAuditLog("logs/homeostat-{date}.jsonl")</code></example>
    public static HomeostatBuilder AddAuditLog(this HomeostatBuilder builder, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return builder.AddAuditLog(o => o.Path = path);
    }

    /// <summary>Records Homeostat events as JSON Lines, with full control over the settings.</summary>
    /// <param name="builder">The Homeostat builder.</param>
    /// <param name="configure">Changes the settings.</param>
    public static HomeostatBuilder AddAuditLog(this HomeostatBuilder builder, Action<AuditLogOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.Configure(configure);
        builder.AddEventSink<AuditLogSink>();
        return builder;
    }
}
