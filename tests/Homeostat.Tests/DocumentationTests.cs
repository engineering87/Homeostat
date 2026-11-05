using System.Globalization;
using Homeostat.AspNetCore;
using Homeostat.Calls;
using Homeostat.Conditions;
using Homeostat.Engine;
using Homeostat.Http;
using Homeostat.Metrics;

namespace Homeostat.Tests;

/// <summary>
/// The limits and timeouts the documentation states are the ones the code uses: a constant changed in the code without
/// the documentation (or the reverse) fails here.
/// </summary>
public class DocumentationTests
{
    private static (string File, string Statement)[] Statements()
    {
        static string N(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
        static string S(TimeSpan value) => value.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        var options = new HomeostatOptions();

        return
        [
            // docs/performance.md, Memory
            ("docs/performance.md", $"`SignalBufferCapacity` signals ({N(options.SignalBufferCapacity)})"),
            ("docs/performance.md", $"About {N(SignalSeries.MaxPoints)} points"),
            ("docs/performance.md", $"window / {N(SignalSeries.MaxPoints)}"),
            ("docs/performance.md", $"Latency samples of a call recorder or an HTTP client | {N(CallRecorder.MaxLatencySamples)} per interval"),
            ("docs/performance.md", $"Samples of a metric | {N(MetricWatch.MaxSamples)} per interval"),
            ("docs/performance.md", $"{N(MetricWatch.MaxSeries)} per instrument"),
            ("docs/performance.md", $"{N(new AlertIngestionOptions().MaxTrackedSources)} sources, {N(AlertState.MaxSeriesPerSource)} firing series per source and {N(AlertState.MaxFiringSeries)} in all"),
            ("docs/performance.md", $"`RunHistorySize` ({options.RunHistorySize}) and `ConditionHistorySize` ({options.ConditionHistorySize})"),
            ("docs/performance.md", $"at most {N(Seasonality.MaxBuckets)} slots"),

            // docs/conditions.md and docs/detectors.md
            ("docs/conditions.md", $"A window keeps about {N(SignalSeries.MaxPoints)} points"),
            ("docs/detectors.md", $"At most {N(MetricWatch.MaxSeries)} series are tracked per instrument"),
            ("docs/detectors.md", $"forgotten after {MetricWatch.MaxMissedCollections} intervals"),

            // docs/integrations.md, alerts
            ("docs/integrations.md", $"at most {N(AlertState.MaxSeriesPerSource)} firing series, and all sources together {N(AlertState.MaxFiringSeries)}"),

            // timeouts
            ("docs/guardrails.md", $"Startup waits at most {S(SharedControlRefresher.StartupWait)} seconds"),
            ("docs/guardrails.md", $"answer within {S(HomeostatEndpointRouteBuilderExtensions.StoreTimeout)} seconds"),
            ("docs/aspnetcore.md", $"waits at most {S(HomeostatEndpointRouteBuilderExtensions.StoreTimeout)} seconds for the store"),
        ];
    }

    [Fact]
    public void The_documentation_states_the_limits_the_code_uses()
    {
        var missing = new List<string>();
        foreach (var (file, statement) in Statements())
        {
            var text = File.ReadAllText(Path.Combine(RepositoryRoot(), file)).ReplaceLineEndings(" ");
            while (text.Contains("  ", StringComparison.Ordinal))
            {
                text = text.Replace("  ", " ", StringComparison.Ordinal);
            }

            if (!text.Contains(statement, StringComparison.Ordinal))
            {
                missing.Add($"{file} does not say: {statement}");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void The_health_check_answers_within_the_documented_second()
        => Assert.True(HealthChecks.HomeostatHealthCheck.StoreTimeout < TimeSpan.FromSeconds(1));

    [Fact]
    public void The_http_client_window_keeps_as_many_samples_as_the_call_recorder()
        => Assert.Equal(CallRecorder.MaxLatencySamples, HttpClientWindow.MaxLatencySamples);

    [Fact]
    public void The_documented_stability_of_each_package_is_the_one_it_is_built_with()
    {
        // Every packed project, preview when it sets HomeostatPreview: the package index, the versioning page and the
        // package's own NuGet readme say the same.
        var root = RepositoryRoot();
        var index = File.ReadAllText(Path.Combine(root, "docs/packages.md"));
        var versioning = File.ReadAllText(Path.Combine(root, "docs/versioning.md")).Split('\n');
        var stableRow = versioning.Single(l => l.StartsWith("| Stable |", StringComparison.Ordinal));
        var previewRow = versioning.Single(l => l.StartsWith("| Preview |", StringComparison.Ordinal));
        var problems = new List<string>();
        foreach (var project in Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var xml = File.ReadAllText(project);
            if (!xml.Contains("<PackageId>", StringComparison.Ordinal) || project.Contains($"{Path.DirectorySeparatorChar}content{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var id = Path.GetFileNameWithoutExtension(project);
            var preview = xml.Contains("<HomeostatPreview>true</HomeostatPreview>", StringComparison.Ordinal);
            var level = preview ? "Preview" : "Stable";
            var row = index.Split('\n').SingleOrDefault(l => l.StartsWith($"| `{id}` |", StringComparison.Ordinal));
            if (row is null || !row.TrimEnd().EndsWith($"| {level} |", StringComparison.Ordinal))
            {
                problems.Add($"docs/packages.md: {id} is not listed as {level}");
            }

            if (!(preview ? previewRow : stableRow).Contains($"`{id}`", StringComparison.Ordinal))
            {
                problems.Add($"docs/versioning.md: {id} is not in the {level} row");
            }

            var readme = Path.Combine(root, "docs/nuget", id + ".md");
            if (File.Exists(readme) && File.ReadAllText(readme).Contains("**preview**", StringComparison.Ordinal) != preview)
            {
                problems.Add($"docs/nuget/{id}.md does not match its stability ({level})");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Homeostat.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (Homeostat.slnx) was not found above " + AppContext.BaseDirectory);
    }
}
