using System.Text.Json;
using Homeostat.Signals;

namespace Homeostat.AspNetCore;

/// <summary>One alert series from a notification: firing or resolved.</summary>
internal sealed record AlertObservation(
    string Source,
    string Key,
    bool Firing,
    SignalSeverity Severity,
    double? Value,
    string Message,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>Alerts or signals parsed from one request, and how many items were skipped.</summary>
internal sealed class AlertBatch
{
    public List<AlertObservation> Alerts { get; } = [];

    public List<Signal> Signals { get; } = [];

    public int Skipped { get; set; }

    /// <summary>Why the payload was rejected as a whole, or <c>null</c>.</summary>
    public string? Invalid { get; set; }

    public int Count => Alerts.Count + Signals.Count;
}

/// <summary>Turns alert notification payloads into alert observations and signals.</summary>
internal static class AlertParsers
{
    private const int MaxTags = 32;
    private const int MaxTagLength = 256;
    private const int MaxSourceLength = 200;

    /// <summary>
    /// Prometheus Alertmanager webhook (version 4) and Grafana alerting webhook, which shares its shape:
    /// <c>{"alerts":[{"status":"firing","labels":{...},"annotations":{...},"fingerprint":"...","values":{...}}]}</c>.
    /// </summary>
    public static AlertBatch ParseAlertmanager(JsonElement root, AlertIngestionOptions options, string origin)
    {
        var batch = new AlertBatch();
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("alerts", out var alerts) || alerts.ValueKind != JsonValueKind.Array)
        {
            batch.Invalid = "The payload has no 'alerts' array.";
            return batch;
        }

        foreach (var alert in alerts.EnumerateArray())
        {
            if (batch.Count >= options.MaxAlertsPerRequest || alert.ValueKind != JsonValueKind.Object)
            {
                batch.Skipped++;
                continue;
            }

            var labels = ReadStrings(alert, "labels");
            if (!labels.TryGetValue(options.SourceLabel, out var name) || !TryMakeSource(options, name, out var source))
            {
                batch.Skipped++;
                continue;
            }

            var firing = !string.Equals(ReadString(alert, "status"), "resolved", StringComparison.OrdinalIgnoreCase);
            labels.TryGetValue(options.SeverityLabel, out var severityLabel);
            var severity = severityLabel is not null && options.UnhealthySeverities.Contains(severityLabel)
                ? SignalSeverity.Unhealthy
                : options.DefaultSeverity;

            var annotations = ReadStrings(alert, "annotations");
            var message = annotations.GetValueOrDefault("summary") ?? annotations.GetValueOrDefault("description")
                ?? $"Alert {name} is {(firing ? "firing" : "resolved")}.";

            var tags = Tags(origin, labels);
            var key = ReadString(alert, "fingerprint") is { Length: > 0 } fingerprint ? fingerprint : LabelKey(labels);
            tags["fingerprint"] = Truncate(key);

            batch.Alerts.Add(new AlertObservation(
                source, key, firing, severity, ReadValue(alert, options.GrafanaValueKey), Truncate(message, 1024), tags));
        }

        return batch;
    }

    /// <summary>
    /// Azure Monitor common alert schema:
    /// <c>{"schemaId":"azureMonitorCommonAlertSchema","data":{"essentials":{"alertRule":"...","severity":"Sev1","monitorCondition":"Fired"}}}</c>.
    /// </summary>
    public static AlertBatch ParseAzureMonitor(JsonElement root, AlertIngestionOptions options)
    {
        var batch = new AlertBatch();
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("essentials", out var essentials) || essentials.ValueKind != JsonValueKind.Object)
        {
            batch.Invalid = "The payload is not in the Azure Monitor common alert schema (data.essentials is missing).";
            return batch;
        }

        var rule = ReadString(essentials, "alertRule");
        if (rule is null || !TryMakeSource(options, rule, out var source))
        {
            batch.Skipped++;
            return batch;
        }

        var condition = ReadString(essentials, "monitorCondition");
        var firing = !string.Equals(condition, "Resolved", StringComparison.OrdinalIgnoreCase);
        var severity = ReadString(essentials, "severity") is "Sev0" or "Sev1" ? SignalSeverity.Unhealthy : options.DefaultSeverity;

        var tags = new Dictionary<string, string>(StringComparer.Ordinal) { ["origin"] = "azure-monitor" };
        foreach (var field in new[] { "severity", "signalType", "monitoringService", "alertId", "essentialsVersion" })
        {
            if (ReadString(essentials, field) is { } value)
            {
                tags[field] = Truncate(value);
            }
        }

        // The alert id is the same in the Fired and Resolved notifications of one alert instance.
        var key = ReadString(essentials, "alertId") is { Length: > 0 } alertId ? alertId : rule;
        batch.Alerts.Add(new AlertObservation(
            source, key, firing, severity, ReadAzureMetricValue(data),
            Truncate(ReadString(essentials, "description") ?? $"Azure Monitor alert {rule} is {condition}.", 1024), tags));
        return batch;
    }

    /// <summary>Generic signals: one object or an array of <c>{"source":"...","severity":"Unhealthy","value":12.5,"message":"..."}</c>.</summary>
    public static AlertBatch ParseSignals(JsonElement root, AlertIngestionOptions options, DateTimeOffset now)
    {
        var batch = new AlertBatch();
        var items = root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().ToArray(),
            JsonValueKind.Object => [root],
            _ => null,
        };
        if (items is null)
        {
            batch.Invalid = "The payload must be a signal object or an array of signal objects.";
            return batch;
        }

        foreach (var item in items)
        {
            if (batch.Count >= options.MaxAlertsPerRequest || item.ValueKind != JsonValueKind.Object
                || ReadString(item, "source") is not { } name || !TryMakeSource(options, name, out var source)
                || ReadString(item, "severity") is not { } severityText || !TryParseSeverity(severityText, out var severity))
            {
                batch.Skipped++;
                continue;
            }

            batch.Signals.Add(new Signal(source, severity, now)
            {
                Value = ReadNumber(item, "value"),
                Message = ReadString(item, "message") is { } message ? Truncate(message, 1024) : null,
                Tags = Tags("signal-endpoint", ReadStrings(item, "tags")),
            });
        }

        return batch;
    }

    /// <summary>Only the names Healthy, Degraded and Unhealthy (any case): no numbers, no combinations.</summary>
    private static bool TryParseSeverity(string text, out SignalSeverity severity)
    {
        foreach (var candidate in Enum.GetValues<SignalSeverity>())
        {
            if (string.Equals(text.Trim(), candidate.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                severity = candidate;
                return true;
            }
        }

        severity = default;
        return false;
    }

    private static bool TryMakeSource(AlertIngestionOptions options, string name, out string source)
    {
        var trimmed = name.Trim();
        source = options.SourcePrefix + trimmed;
        return trimmed.Length > 0 && source.Length <= MaxSourceLength;
    }

    /// <summary>Identity of an alert series without a fingerprint: its sorted labels.</summary>
    private static string LabelKey(Dictionary<string, string> labels)
        => string.Join('\u001f', labels.OrderBy(l => l.Key, StringComparer.Ordinal).Select(l => $"{l.Key}={l.Value}"));

    private static Dictionary<string, string> Tags(string origin, Dictionary<string, string> labels)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in labels.Take(MaxTags))
        {
            tags[Truncate(key)] = Truncate(value);
        }

        tags["origin"] = origin;
        return tags;
    }

    private static Dictionary<string, string> ReadStrings(JsonElement parent, string property)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parent.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in element.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { } value)
                {
                    result[entry.Name] = value;
                }
            }
        }

        return result;
    }

    private static string? ReadString(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static double? ReadNumber(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : null;

    /// <summary>
    /// Grafana sends the evaluated query values as <c>"values":{"B":12.5,"C":1}</c>: the value of
    /// <paramref name="key"/> when set, otherwise the only numeric value, if there is exactly one.
    /// </summary>
    private static double? ReadValue(JsonElement alert, string? key)
    {
        if (!alert.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (key is not null)
        {
            return ReadNumber(values, key);
        }

        double? found = null;
        foreach (var entry in values.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetDouble(out var number) || !double.IsFinite(number))
            {
                continue;
            }

            if (found is not null)
            {
                return null; // Several values: which one matters is ambiguous; set GrafanaValueKey.
            }

            found = number;
        }

        return found;
    }

    /// <summary>Metric alerts carry <c>data.alertContext.condition.allOf[0].metricValue</c>.</summary>
    private static double? ReadAzureMetricValue(JsonElement data)
    {
        if (data.TryGetProperty("alertContext", out var context) && context.ValueKind == JsonValueKind.Object
            && context.TryGetProperty("condition", out var condition) && condition.ValueKind == JsonValueKind.Object
            && condition.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array
            && allOf.GetArrayLength() == 1 && allOf[0].ValueKind == JsonValueKind.Object)
        {
            return ReadNumber(allOf[0], "metricValue");
        }

        return null;
    }

    private static string Truncate(string value, int max = MaxTagLength)
        => value.Length <= max ? value : value[..max];
}
