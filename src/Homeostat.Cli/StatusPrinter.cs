using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Homeostat.Cli;

/// <summary>Formats the status endpoint's JSON for a terminal.</summary>
internal static class StatusPrinter
{
    /// <summary>
    /// Removes control characters other than line breaks: condition names and messages can come from external alerts,
    /// and escape sequences must not reach the operator's terminal.
    /// </summary>
    internal static string Clean(string text)
        => string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                // A carriage return would rewind the line and let an alert text overwrite the status column.
                span[i] = c is '\n' || !(char.IsControl(c) || c is '\u2028' or '\u2029') ? c : ' ';
            }
        });

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
                    $"  {a.GetProperty("id").GetString()}  {a.GetProperty("conditionName").GetString()}: {a.GetProperty("stepName").GetString()}{GroupSuffix(a)}");
            }

            text.AppendLine("  Answer with: homeostat approve <id>  or  homeostat reject <id>");
        }

        // Healing groups: only when the application has some (the status then has a "groups" list).
        if (status.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array && groups.GetArrayLength() > 0)
        {
            text.AppendLine().AppendLine("Groups:");
            var list = groups.EnumerateArray().ToList();
            var groupWidth = list.Max(g => g.GetProperty("name").GetString()!.Length);
            foreach (var g in list)
            {
                var state = g.GetProperty("paused").GetBoolean() ? "paused" : "healing";
                var parts = new List<string>();
                if (g.TryGetProperty("budget", out var groupBudget) && groupBudget.ValueKind == JsonValueKind.Object)
                {
                    var used = groupBudget.GetProperty("used").GetInt32();
                    var limit = groupBudget.GetProperty("limit").GetInt32();
                    parts.Add(used >= limit ? $"budget {used}/{limit} EXHAUSTED" : $"budget {used}/{limit}");
                }
                else if (g.TryGetProperty("maxActionsPerWindow", out var max) && max.ValueKind == JsonValueKind.Number)
                {
                    parts.Add($"budget ?/{max.GetInt32()}");
                }

                if (g.TryGetProperty("pauseReason", out var groupReason) && groupReason.ValueKind == JsonValueKind.String)
                {
                    parts.Add(groupReason.GetString()!);
                }

                var line = string.Create(CultureInfo.InvariantCulture, $"  {g.GetProperty("name").GetString()!.PadRight(groupWidth)}  {state,-8}  {string.Join("; ", parts)}");
                text.AppendLine(line.TrimEnd());
            }

            text.AppendLine("  Pause or resume one with: homeostat pause --group <name>  or  homeostat resume --group <name>");
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
            text.AppendLine(CultureInfo.InvariantCulture, $"  {name.PadRight(width)}  {state,-8}  {detail}{GroupSuffix(c)}");
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
                    $"  {completed}  {r.GetProperty("condition").GetString()}  {r.GetProperty("outcome").GetString()}{GroupSuffix(r)}");
            }
        }

        return Clean(text.ToString());
    }

    // " (group x)" for a condition or an approval in a healing group; nothing otherwise, so the output without groups is unchanged.
    private static string GroupSuffix(JsonElement element)
        => element.TryGetProperty("group", out var group) && group.ValueKind == JsonValueKind.String ? $" (group {group.GetString()})" : string.Empty;

    public static string PrintCoverage(JsonElement report)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"Coverage since {report.GetProperty("since").GetDateTimeOffset().ToLocalTime():yyyy-MM-dd HH:mm}: {report.GetProperty("stepCoverage").GetDouble():P0} of plan steps ran");
        text.AppendLine();
        var conditions = report.GetProperty("conditions").EnumerateArray().ToList();
        var width = conditions.Count == 0 ? 9 : Math.Max(9, conditions.Max(c => c.GetProperty("name").GetString()!.Length));
        text.AppendLine(CultureInfo.InvariantCulture, $"  {"Condition".PadRight(width)}  Activations  Runs  Outcomes");
        foreach (var c in conditions)
        {
            var outcomes = string.Join(", ", c.GetProperty("outcomes").EnumerateObject().Select(o => $"{o.Name} {o.Value.GetInt32()}"));
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {c.GetProperty("name").GetString()!.PadRight(width)}  {c.GetProperty("activations").GetInt32(),11}  {c.GetProperty("runs").GetInt32(),4}  {outcomes}");
            var failed = c.TryGetProperty("stepsFailed", out var f) ? f.EnumerateArray().Select(s => s.GetString()).ToList() : [];
            if (failed.Count > 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {string.Empty.PadRight(width)}  always failed: {string.Join(", ", failed)}");
            }

            var never = c.GetProperty("stepsNeverRun").EnumerateArray().Select(s => s.GetString()).ToList();
            if (never.Count > 0)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {string.Empty.PadRight(width)}  never ran: {string.Join(", ", never)}");
            }
        }

        var neverActivated = report.GetProperty("neverActivated").EnumerateArray().Select(s => s.GetString()).ToList();
        if (neverActivated.Count > 0)
        {
            text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Never activated: {string.Join(", ", neverActivated)}");
        }

        if (report.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
        {
            text.AppendLine().AppendLine("Warning: the run history does not reach back to the start of the period; older runs are not counted.");
        }

        return Clean(text.ToString());
    }

    public static string PrintHistory(JsonElement events)
    {
        var text = new StringBuilder();
        var list = events.EnumerateArray().ToList();
        if (list.Count == 0)
        {
            return "No condition events." + Environment.NewLine;
        }

        var width = Math.Max(9, list.Max(e => e.GetProperty("condition").GetString()!.Length));
        foreach (var e in list)
        {
            var at = e.GetProperty("at").GetDateTimeOffset().ToLocalTime();
            var detail = e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {at:yyyy-MM-dd HH:mm:ss}  {e.GetProperty("condition").GetString()!.PadRight(width)}  {e.GetProperty("kind").GetString(),-9}  {e.GetProperty("instance").GetString()}{(detail is null ? string.Empty : $"  {detail}")}");
        }

        return Clean(text.ToString());
    }
}
