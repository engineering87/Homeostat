using Homeostat.Conditions;
using Homeostat.Consumers;
using Homeostat.Samples.Architectures.ForecastApi;
using Homeostat.Samples.Architectures.TelemetryIngestion;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Homeostat.Tests;

/// <summary>
/// The worked examples of docs/architecture-examples.md behave as the page says: each test is one sentence of the
/// page, run on simulated time against the real configuration of the sample.
/// </summary>
public sealed class ArchitectureExamplesTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Forecast_primary_down_serves_stale_data_then_asks_before_failing_over_and_goes_back_when_it_recovers()
    {
        var providers = new FakeProviders();
        await using var host = await StartForecastAsync(providers, new FakeCache());

        providers.Down = true;
        var request = await host.WaitForApprovalAsync("forecast-primary-down", Wait);
        Assert.True(host.Switches.IsOn(ForecastApiHealing.ServeStaleForecasts));
        Assert.False(host.Switches.IsOn(ForecastApiHealing.UseBackupProvider));

        Assert.True(await host.Approvals.ApproveAsync(request.Id, "on-call"));
        await WaitUntilAsync(() => host.Switches.IsOn(ForecastApiHealing.UseBackupProvider));

        providers.Down = false;
        await host.WaitForConditionAsync("forecast-primary-down", ConditionStatus.Inactive, Wait);
        await WaitUntilAsync(() => !host.Switches.IsOn(ForecastApiHealing.UseBackupProvider)
            && !host.Switches.IsOn(ForecastApiHealing.ServeStaleForecasts));
    }

    [Fact]
    public async Task Forecast_cache_down_bypasses_the_cache_and_does_not_fail_over_the_provider_it_overloads()
    {
        var providers = new FakeProviders();
        var cache = new FakeCache();
        await using var host = await StartForecastAsync(providers, cache);
        var start = host.Time.GetUtcNow();

        cache.Down = true;
        providers.Down = true;
        await WaitUntilAsync(() => host.Switches.IsOn(ForecastApiHealing.BypassCache));
        await WaitUntilAsync(() => host.Time.GetUtcNow() > start + TimeSpan.FromMinutes(6));

        Assert.False(host.Switches.IsOn(ForecastApiHealing.ServeStaleForecasts));
        Assert.False(host.Switches.IsOn(ForecastApiHealing.UseBackupProvider));
        Assert.Empty(await host.Approvals.GetPendingAsync());
    }

    [Fact]
    public async Task Geocoder_down_hides_place_names_and_tries_again_after_ten_minutes()
    {
        await using var host = await StartForecastAsync(new FakeProviders(), new FakeCache());
        DateTimeOffset? on = null, off = null;
        host.Switches.Changed += (_, e) =>
        {
            if (e.Name == ForecastApiHealing.HidePlaceNames)
            {
                // Simulated time when the switch changed, not when the test noticed it.
                if (e.IsOn)
                {
                    on ??= host.Time.GetUtcNow();
                }
                else
                {
                    off ??= host.Time.GetUtcNow();
                }
            }
        };

        // The HTTP client signal: failing calls while the names are shown, then no calls (no signal) once hidden.
        while (!host.Switches.IsOn(ForecastApiHealing.HidePlaceNames))
        {
            host.Publish("http:geocoder", SignalSeverity.Unhealthy);
            var next = host.Time.GetUtcNow() + TimeSpan.FromSeconds(10);
            await WaitUntilAsync(() => host.Time.GetUtcNow() >= next || host.Switches.IsOn(ForecastApiHealing.HidePlaceNames));
        }

        await WaitUntilAsync(() => off is not null);
        Assert.True(off - on >= TimeSpan.FromMinutes(10), $"The names came back after {off - on}.");
    }

    [Fact]
    public async Task Telemetry_store_down_pauses_the_ingestion_and_resumes_it_when_the_store_is_back()
    {
        var store = new FakeStore();
        await using var host = await StartTelemetryAsync(store, new FakeStats());
        var gate = host.Services.GetRequiredKeyedService<PauseGate>(TelemetryIngestionHealing.Consumer);

        store.Down = true;
        await WaitUntilAsync(() => gate.IsPaused);

        store.Down = false;
        await WaitUntilAsync(() => !gate.IsPaused);
    }

    [Fact]
    public async Task Telemetry_store_slow_sheds_diagnostic_readings_and_the_lag_it_causes_changes_nothing_else()
    {
        var store = new FakeStore();
        var stats = new FakeStats();
        await using var host = await StartTelemetryAsync(store, stats);
        var start = host.Time.GetUtcNow();

        store.Latency = TimeSpan.FromMilliseconds(400);
        stats.GrowthPerMinute = 5_000;
        await WaitUntilAsync(() => host.Switches.IsOn(TelemetryIngestionHealing.SkipDiagnosticReadings));
        await WaitUntilAsync(() => host.Time.GetUtcNow() > start + TimeSpan.FromMinutes(15));

        Assert.False(host.Switches.IsOn(TelemetryIngestionHealing.LargerWriteBatches));
        Assert.False(host.Services.GetRequiredKeyedService<PauseGate>(TelemetryIngestionHealing.Consumer).IsPaused);
    }

    [Fact]
    public async Task Telemetry_lag_growing_while_the_store_is_fine_writes_larger_batches()
    {
        var stats = new FakeStats();
        await using var host = await StartTelemetryAsync(new FakeStore(), stats);

        stats.GrowthPerMinute = 5_000;
        await WaitUntilAsync(() => host.Switches.IsOn(TelemetryIngestionHealing.LargerWriteBatches));
        Assert.False(host.Switches.IsOn(TelemetryIngestionHealing.SkipDiagnosticReadings));
    }

    [Fact]
    public void The_page_shows_the_code_of_the_sample()
    {
        var root = RepositoryRoot();
        var page = File.ReadAllText(Path.Combine(root, "docs", "architecture-examples.md"));
        var sample = string.Join('\n', Directory
            .EnumerateFiles(Path.Combine(root, "samples", "Homeostat.Samples.Architectures"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0));

        var blocks = page.Split("```csharp").Skip(1).Select(block => block[..block.IndexOf("```", StringComparison.Ordinal)]).ToList();
        Assert.NotEmpty(blocks);
        foreach (var block in blocks.Where(block => !block.Contains("builder.Services", StringComparison.Ordinal)))
        {
            // Indentation differs between the page and the sources; the lines, in order, must not.
            var lines = string.Join('\n', block.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
            Assert.True(sample.Contains(lines, StringComparison.Ordinal), "Not found in the sample:\n" + lines);
        }
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

    private static async Task<HomeostatTestHost> StartForecastAsync(FakeProviders providers, FakeCache cache)
    {
        var host = await HomeostatTestHost.StartAsync(
            h => h.AddForecastApiHealing(),
            services => services
                .AddSingleton<IForecastProviders>(providers)
                .AddSingleton<IForecastCache>(cache));
        return host;
    }

    private static async Task<HomeostatTestHost> StartTelemetryAsync(FakeStore store, FakeStats stats)
    {
        var host = await HomeostatTestHost.StartAsync(
            h => h.AddTelemetryIngestionHealing(),
            services => services
                .AddSingleton<ITimeSeriesStore>(store)
                .AddSingleton<IIngestionStats>(stats));
        store.Time = host.Time;
        stats.Time = host.Time;
        stats.Start = host.Time.GetUtcNow();
        return host;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeProviders : IForecastProviders
    {
        public volatile bool Down;

        public Task PingPrimaryAsync(CancellationToken cancellationToken)
            => Down ? throw new HttpRequestException("The primary provider does not answer.") : Task.CompletedTask;
    }

    private sealed class FakeCache : IForecastCache
    {
        public volatile bool Down;

        public Task PingAsync(CancellationToken cancellationToken)
            => Down ? throw new IOException("The cache cannot be reached.") : Task.CompletedTask;
    }

    private sealed class FakeStore : ITimeSeriesStore
    {
        public volatile bool Down;
        public TimeProvider Time = TimeProvider.System;
        public TimeSpan Latency;

        public Task PingAsync(CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new IOException("The store cannot be reached.");
            }

            return Latency > TimeSpan.Zero ? Task.Delay(Latency, Time, cancellationToken) : Task.CompletedTask;
        }

        public Task WriteAsync(IReadOnlyList<Reading> readings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeStats : IIngestionStats
    {
        public TimeProvider Time = TimeProvider.System;
        public DateTimeOffset Start;
        public double GrowthPerMinute;

        public Task<double> GetLagAsync(CancellationToken cancellationToken)
            => Task.FromResult(1_000 + Math.Max(0, (Time.GetUtcNow() - Start).TotalMinutes) * GrowthPerMinute);
    }
}
