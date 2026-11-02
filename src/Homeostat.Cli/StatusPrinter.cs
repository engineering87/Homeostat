using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Homeostat.Cli;

/// <summary>Formats the status endpoint's JSON for a terminal.</summary>
internal static class StatusPrinter
{
    public static string Print(JsonElement status)
    {
        var text = new StringBuilder();
        var flags = new List<string>();
        if (status.GetProperty("paused").GetBoolean())
        {
            flags.Add(status.TryGetProperty("pauseReason", out var reason) && reason.ValueKind == JsonValueKind.String
                ? $"PAUSED ({reason.GetString()})" : "PAUSED");
        }

        if (status.GetProperty("dryRun").GetBoolean())
        {
            flags.Add("DRY-RUN");
        }

        text.Append(CultureInfo.InvariantCulture, $"{status.GetProperty("application").GetString()} on {status.GetProperty("instance").GetString()}");
        text.AppendLine(flags.Count > 0 ? "  " + string.Join("  ", flags) : string.Empty);

        if (status.TryGetProperty("storeError", out var storeError) && storeError.ValueKind == JsonValueKind.String)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"! Store unavailable, Homeostat does not act: {storeError.GetString()}");
        }

        if (status.TryGetProperty("budget", out var budget) && budget.ValueKind == JsonValueKind.Object)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"Budget: {budget.GetProperty("used").GetInt32()}/{budget.GetProperty("limit").GetInt32()} actions used");
        }

        var approvals = status.GetProperty("pendingApprovals").EnumerateArray().ToList();
        if (approvals.Count > 0)
        {
            text.AppendLine().AppendLine("Waiting for approval:");
            foreach (var a in approvals)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"  {a.GetProperty("id").GetString()}  {a.GetProperty("conditionName").GetString()}: {a.GetProperty("stepName").GetString()}");
            }

            text.AppendLine("  Answer with: homeostat approve <id>  or  homeostat reject <id>");
        }

        text.AppendLine().AppendLine("Conditions:");
        var conditions = status.GetProperty("conditions").EnumerateArray()
            .OrderBy(c => c.GetProperty("status").GetString() switch { "Active" => 0, "Pending" => 1, _ => 2 })
            .ThenBy(c => c.GetProperty("name").GetString(), StringComparer.Ordinal)
            .ToList();
        var width = conditions.Count == 0 ? 0 : conditions.Max(c => c.GetProperty("name").GetString()!.Length);
        foreach (var c in conditions)
        {
            var name = c.GetProperty("name").GetString()!;
            var state = c.GetProperty("status").GetString()!;
            var detail = c.TryGetProperty("suppressedBy", out var s) && s.ValueKind == JsonValueKind.String ? $"suppressed by {s.GetString()}"
                : c.TryGetProperty("lastMessage", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()
                : string.Empty;
            text.AppendLine(CultureInfo.InvariantCulture, $"  {name.PadRight(width)}  {state,-8}  {detail}");
        }

        if (conditions.Count == 0)
        {
            text.AppendLine("  (none)");
        }

        var switches = status.GetProperty("switches").EnumerateArray().Select(s => s.GetString()).ToList();
        text.AppendLine().AppendLine(switches.Count > 0 ? $"Switches on: {string.Join(", ", switches)}" : "Switches on: none");

        var runs = status.GetProperty("recentRuns").EnumerateArray().Take(5).ToList();
        if (runs.Count > 0)
        {
            text.AppendLine().AppendLine("Recent runs:");
            foreach (var r in runs)
            {
                var completed = r.GetProperty("completedAt").GetDateTimeOffset().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"  {completed}  {r.GetProperty("condition").GetString()}  {r.GetProperty("outcome").GetString()}");
            }
        }

        return text.ToString();
    }
}
