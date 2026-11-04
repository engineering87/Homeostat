using Homeostat.Signals;

namespace Homeostat.AspNetCore;

/// <summary>Settings of the alert endpoints mapped with <c>MapHomeostatAlerts</c>.</summary>
public sealed class AlertIngestionOptions
{
    /// <summary>
    /// Shared secret that callers send as <c>Authorization: Bearer {token}</c>. The Azure Monitor endpoint also accepts
    /// it as the <c>token</c> query parameter, because plain action group webhooks cannot set headers (the URL may then
    /// appear in access logs). When set, it protects the endpoints in place of (or in addition to) an authorization
    /// policy. Use at least 16 characters of random data, and HTTPS.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// Label whose value becomes the signal source for Prometheus Alertmanager and Grafana alerts. Defaults to
    /// <c>alertname</c>. Alerts without the label are skipped.
    /// </summary>
    public string SourceLabel { get; set; } = "alertname";

    /// <summary>
    /// Prefix added to every signal source. Defaults to <c>"alert:"</c>, so an alert named <c>DiskAlmostFull</c>
    /// becomes the source <c>alert:DiskAlmostFull</c>: alerts cannot impersonate the signals of detectors and probes.
    /// </summary>
    public string SourcePrefix { get; set; } = "alert:";

    /// <summary>
    /// How long a firing alert stays firing without being notified again. Some senders never resolve (Azure Monitor
    /// stateless log alerts) or the resolution can be lost; after this time the alert counts as resolved. Keep it
    /// longer than the repeat interval of the sender (Alertmanager <c>repeat_interval</c>, 4 hours by default).
    /// Defaults to 12 hours.
    /// </summary>
    public TimeSpan FiringTimeToLive { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Most distinct sources tracked; alerts for further sources are skipped. Defaults to 1000.</summary>
    public int MaxTrackedSources { get; set; } = 1000;

    /// <summary>
    /// Grafana query whose value becomes the signal value, for example <c>"B"</c>. When not set, the value is used only
    /// if the alert carries exactly one numeric value.
    /// </summary>
    public string? GrafanaValueKey { get; set; }

    /// <summary>Label read for the severity of Alertmanager and Grafana alerts. Defaults to <c>severity</c>.</summary>
    public string SeverityLabel { get; set; } = "severity";

    /// <summary>
    /// Severity label values mapped to <see cref="SignalSeverity.Unhealthy"/> (case-insensitive). Other values,
    /// and alerts without the label, map to <see cref="DefaultSeverity"/>.
    /// </summary>
    public ISet<string> UnhealthySeverities { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "critical", "error", "page", "high", "major", "disaster", "fatal",
    };

    /// <summary>Severity of a firing alert whose severity is not in <see cref="UnhealthySeverities"/>. Defaults to <see cref="SignalSeverity.Degraded"/>.</summary>
    public SignalSeverity DefaultSeverity { get; set; } = SignalSeverity.Degraded;

    /// <summary>Largest request body accepted, in bytes. Defaults to 1 MiB.</summary>
    public long MaxRequestBodySize { get; set; } = 1024 * 1024;

    /// <summary>Most alerts accepted in one request; the rest are skipped. Defaults to 500.</summary>
    public int MaxAlertsPerRequest { get; set; } = 500;

    internal void Validate()
    {
        Token = Token?.Trim();
        if (Token is not null && Token.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Token cannot contain white space.");
        }

        if (DefaultSeverity is not (SignalSeverity.Degraded or SignalSeverity.Unhealthy))
        {
            throw new ArgumentException("DefaultSeverity must be Degraded or Unhealthy: it is the severity of a firing alert.");
        }

        if (FiringTimeToLive <= TimeSpan.Zero || FiringTimeToLive > TimeSpan.FromDays(49) || MaxTrackedSources < 1)
        {
            throw new ArgumentException("FiringTimeToLive must be positive and at most about 49 days, and MaxTrackedSources at least 1.");
        }

        if (string.IsNullOrWhiteSpace(SourceLabel) || string.IsNullOrWhiteSpace(SeverityLabel))
        {
            throw new ArgumentException("SourceLabel and SeverityLabel cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(SourcePrefix))
        {
            // Without a prefix, a token holder could publish signals that look like the detectors' own ("threadpool").
            throw new ArgumentException("SourcePrefix cannot be empty: alerts must not be able to pose as the application's own signals.");
        }

        if (Token is not null && Token.Length < 16)
        {
            throw new ArgumentException("Token must be at least 16 characters long.");
        }

        if (MaxRequestBodySize < 1024 || MaxAlertsPerRequest < 1)
        {
            throw new ArgumentException("MaxRequestBodySize must be at least 1024 bytes and MaxAlertsPerRequest at least 1.");
        }
    }
}
