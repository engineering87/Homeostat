using System.Text.Json;
using Homeostat.Testing;
using Microsoft.Extensions.Configuration;

namespace Homeostat.Cli;

/// <summary>The <c>replay</c> command: recorded signals against a configuration file, without the application.</summary>
internal static class Replay
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<ReplayReport> RunAsync(
        IEnumerable<string> inputs, string configFile, string section, string optionsSection, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                paths.AddRange(Directory.GetFiles(input, "*.jsonl"));
            }
            else if (File.Exists(input))
            {
                paths.Add(input);
            }
            else
            {
                throw new CliException($"Recording not found: {input}");
            }
        }

        if (paths.Count == 0)
        {
            throw new CliException("No recording files (*.jsonl) found.");
        }

        if (!File.Exists(configFile))
        {
            throw new CliException($"Configuration file not found: {configFile}");
        }

        IConfiguration configuration;
        try
        {
            configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(configFile), optional: false, reloadOnChange: false).Build();
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or JsonException)
        {
            throw new CliException($"The configuration file is not valid JSON: {ex.Message}");
        }

        var conditions = configuration.GetSection(section);
        if (!conditions.GetChildren().Any())
        {
            throw new CliException($"No conditions in section '{section}' of {configFile}.");
        }

        // Cooldowns, budget and concurrency change how often a plan would run: use the application's own.
        var options = configuration.GetSection(optionsSection);
        Action<HomeostatOptions>? bind = options.Exists() ? o => options.Bind(o) : null;

        try
        {
            return await HomeostatReplay.RunFilesAsync(paths, conditions, bind, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or TimeZoneNotFoundException)
        {
            throw new CliException($"The conditions cannot be loaded: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"A recording cannot be read: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            throw new CliException("Replay cancelled.");
        }
    }

    public static string ToJson(ReplayReport report) => JsonSerializer.Serialize(new
    {
        report.From,
        report.To,
        report.Signals,
        Conditions = report.Conditions.Select(c => new { c.Condition, c.Activations, c.WouldHeal, c.PlanSteps }),
        Events = report.Events.Select(e => new { e.At, e.Condition, Kind = e.Kind.ToString(), e.Detail }),
    }, Json);
}
