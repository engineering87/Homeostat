namespace Homeostat.Notifications;

/// <summary>Settings of the JSON Lines audit log (see <see cref="HomeostatAuditLogExtensions.AddAuditLog(HomeostatBuilder, string)"/>).</summary>
public sealed class AuditLogOptions
{
    /// <summary>
    /// File to append to. A <c>{date}</c> placeholder is replaced with the UTC date (<c>yyyy-MM-dd</c>), which
    /// starts a new file every day. Relative paths are resolved against the current directory. Defaults to
    /// <c>"homeostat-audit-{date}.jsonl"</c>.
    /// </summary>
    public string Path { get; set; } = "homeostat-audit-{date}.jsonl";

    /// <summary>Events to record. All kinds by default.</summary>
    public ISet<HomeostatEventKind> Events { get; } = new HashSet<HomeostatEventKind>(Enum.GetValues<HomeostatEventKind>());
}
