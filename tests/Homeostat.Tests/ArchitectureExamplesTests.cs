using System.Collections.Concurrent;
using Homeostat.Conditions;
using Homeostat.Consumers;
using Homeostat.Healing;
using Homeostat.Samples.Architectures.ForecastApi;
using Homeostat.Samples.Architectures.ModularMonolith;
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
    public async Task Modular_monolith_thread_pool_starvation_pauses_reports_then_lowers_tile_resolution_and_never_touches_alerts()
    {
        await using var host = await StartMonolithAsync(new MonolithFakes());
        var changes = new ConcurrentQueue<(string Name, bool IsOn)>();
        host.Switches.Changed += (_, e) => changes.Enqueue((e.Name, e.IsOn));

        await PublishUntilAsync(host, "threadpool", SignalSeverity.Unhealthy, () => host.Switches.IsOn(TileRenderer.LowResolution));
        Assert.Equal([(ReportsHealing.Paused, true), (TileRenderer.LowResolution, true)], changes.ToArray());

        // The pool recovers: the modules get their work back once it has been healthy for five minutes.
        await PublishUntilAsync(host, "threadpool", SignalSeverity.Healthy,
            () => !host.Switches.IsOn(ReportsHealing.Paused) && !host.Switches.IsOn(TileRenderer.LowResolution));
        Assert.All(changes, change => Assert.Contains(change.Name, new[] { ReportsHealing.Paused, TileRenderer.LowResolution }));
    }

    [Fact]
    public async Task Modular_monolith_an_exhausted_reports_budget_does_not_stop_the_forecasts_plan()
    {
        var fakes = new MonolithFakes();
        await using var host = await StartMonolithAsync(fakes);
        var start = host.Time.GetUtcNow();

        // New connections do not help: without its own budget, the module would spend the 10 actions of the process
        // in about half an hour.
        fakes.ReportsReader.Down = true;
        await WaitUntilAsync(() => host.Time.GetUtcNow() > start + TimeSpan.FromMinutes(35));
        Assert.Equal(2, fakes.ReportsReader.Resets);
        Assert.Contains(host.CompletedRuns, r => r.ConditionName == "reports-forecast-reads-failing" && r.Outcome == HealingRunOutcome.BudgetExhausted);

        fakes.Provider.Down = true;
        await WaitUntilAsync(() => host.Switches.IsOn(ForecastsHealing.ServeLastForecasts));
    }

    [Fact]
    public async Task Modular_monolith_a_long_run_in_reports_does_not_hold_the_other_modules()
    {
        var fakes = new MonolithFakes();
        await using var host = await StartMonolithAsync(fakes);
        try
        {
            // Stale statistics: the refresh starts and lasts.
            fakes.ReportsDb.Latency = TimeSpan.FromMilliseconds(800);
            await WaitUntilAsync(() => fakes.ReportsDb.Refreshes == 1);

            fakes.ReportsReader.Down = true;
            fakes.Provider.Down = true;
            await WaitUntilAsync(() => host.Switches.IsOn(ForecastsHealing.ServeLastForecasts));
            await host.WaitForConditionAsync("reports-forecast-reads-failing", ConditionStatus.Active, Wait);
            var active = host.Time.GetUtcNow();
            await WaitUntilAsync(() => host.Time.GetUtcNow() > active + TimeSpan.FromMinutes(1));

            // The second run of reports waits for the first one; forecasts did not wait for either.
            Assert.Equal(0, fakes.ReportsReader.Resets);
        }
        finally
        {
            fakes.ReportsDb.FinishRefresh();
        }

        await WaitUntilAsync(() => fakes.ReportsReader.Resets > 0);
    }

    [Fact]
    public async Task Modular_monolith_the_reports_maintenance_window_pauses_only_reports()
    {
        var fakes = new MonolithFakes();
        // Sunday 6 January 2030 at 03:50 UTC: ten minutes before the end of the reindex window.
        var sunday = new DateTimeOffset(2030, 1, 6, 3, 50, 0, TimeSpan.Zero);
        await using var host = await StartMonolithAsync(fakes, new HomeostatTestHostOptions { StartTime = sunday });
        var end = sunday + TimeSpan.FromMinutes(10);

        fakes.ReportsDb.Latency = TimeSpan.FromMilliseconds(800);
        fakes.Provider.Down = true;
        await WaitUntilAsync(() => host.Switches.IsOn(ForecastsHealing.ServeLastForecasts));
        Assert.True(host.Control.IsGroupPaused(ReportsHealing.Group));
        Assert.False(host.Control.IsGroupPaused(ForecastsHealing.Group));
        Assert.False(host.Control.IsPaused);

        await host.WaitForConditionAsync("reports-db-slow", ConditionStatus.Active, Wait);
        await WaitUntilAsync(() => host.Time.GetUtcNow() >= end - TimeSpan.FromMinutes(1));
        Assert.Equal(0, fakes.ReportsDb.Refreshes);

        fakes.ReportsDb.FinishRefresh();
        await WaitUntilAsync(() => fakes.ReportsDb.Refreshes > 0);
        Assert.True(fakes.ReportsDb.FirstRefreshAt >= end, $"The refresh started at {fakes.ReportsDb.FirstRefreshAt}, inside the window.");
    }

    [Fact]
    public async Task Modular_monolith_with_the_forecast_store_down_the_store_is_reopened_reports_wait_and_alerts_use_their_snapshot()
    {
        var fakes = new MonolithFakes();
        await using var host = await StartMonolithAsync(fakes);

        // Every module that reads the store fails with it.
        fakes.Store.Down = true;
        fakes.ReportsReader.Down = true;
        fakes.AlertsReader.Down = true;
        await WaitUntilAsync(() => fakes.Store.Reopens > 0 && host.Switches.IsOn(AlertsHealing.EvaluateOnSnapshot));
        var since = host.Time.GetUtcNow();
        await WaitUntilAsync(() => host.Time.GetUtcNow() > since + TimeSpan.FromMinutes(5));
        Assert.Equal(ConditionStatus.Active, host.Control.GetConditions().Single(c => c.Name == "reports-forecast-reads-failing").Status);
        Assert.Equal(0, fakes.ReportsReader.Resets);

        fakes.Store.Down = false;
        fakes.ReportsReader.Down = false;
        fakes.AlertsReader.Down = false;
        await WaitUntilAsync(() => !host.Switches.IsOn(AlertsHealing.EvaluateOnSnapshot));
        Assert.Equal(0, fakes.ReportsReader.Resets);
    }

    [Fact]
    public async Task Modular_monolith_pausing_the_reports_module_leaves_the_other_modules_healing()
    {
        var fakes = new MonolithFakes();
        await using var host = await StartMonolithAsync(fakes);
        var incidents = new ModuleIncidents(host.Control, host.Time);

        await incidents.PauseHealingAsync(ReportsHealing.Group, TimeSpan.FromHours(1), "on-call");
        fakes.ReportsReader.Down = true;
        fakes.Gateway.Down = true;
        await WaitUntilAsync(() => host.Switches.IsOn(AlertsHealing.SecondaryGateway));
        await host.WaitForConditionAsync("reports-forecast-reads-failing", ConditionStatus.Active, Wait);
        var active = host.Time.GetUtcNow();
        await WaitUntilAsync(() => host.Time.GetUtcNow() > active + TimeSpan.FromMinutes(1));
        Assert.Equal(0, fakes.ReportsReader.Resets);

        await incidents.ResumeHealingAsync(ReportsHealing.Group, "on-call");
        await WaitUntilAsync(() => fakes.ReportsReader.Resets > 0);
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

    private static async Task<HomeostatTestHost> StartMonolithAsync(MonolithFakes fakes, HomeostatTestHostOptions? options = null)
    {
        var host = await HomeostatTestHost.StartAsync(
            h => h.AddProcessHealing(),
            services =>
            {
                services
                    .AddSingleton<IForecastProvider>(fakes.Provider)
                    .AddSingleton<IForecastStore>(fakes.Store)
                    .AddSingleton<IAlertGateway>(fakes.Gateway)
                    .AddSingleton<IAlertsForecastReader>(fakes.AlertsReader)
                    .AddSingleton<IReportsDatabase>(fakes.ReportsDb)
                    .AddSingleton<IReportsForecastReader>(fakes.ReportsReader);

                // Each module adds its own knowledge, as in Program.cs.
                services.AddHomeostat(h => h.AddForecastsHealing());
                services.AddHomeostat(h => h.AddAlertsHealing());
                services.AddHomeostat(h => h.AddReportsHealing());
            },
            options);
        fakes.ReportsDb.Time = host.Time;
        return host;
    }

    /// <summary>Publishes the signal every 10 simulated seconds until the condition holds.</summary>
    private static async Task PublishUntilAsync(HomeostatTestHost host, string source, SignalSeverity severity, Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Wait);
        while (!condition())
        {
            host.Publish(source, severity);
            var next = host.Time.GetUtcNow() + TimeSpan.FromSeconds(10);
            while (host.Time.GetUtcNow() < next && !condition())
            {
                await Task.Delay(10, timeout.Token);
            }
        }
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

    private sealed class MonolithFakes
    {
        public FakeComponent Provider { get; } = new();
        public FakeForecastStore Store { get; } = new();
        public FakeComponent Gateway { get; } = new();
        public FakeComponent AlertsReader { get; } = new();
        public FakeReportsDatabase ReportsDb { get; } = new();
        public FakeReportsReader ReportsReader { get; } = new();
    }

    private sealed class FakeComponent : IForecastProvider, IAlertGateway, IAlertsForecastReader
    {
        public volatile bool Down;

        public Task PingAsync(CancellationToken cancellationToken)
            => Down ? throw new IOException("The component does not answer.") : Task.CompletedTask;
    }

    private sealed class FakeForecastStore : IForecastStore
    {
        public volatile bool Down;
        private int _reopens;

        public int Reopens => Volatile.Read(ref _reopens);

        public Task PingAsync(CancellationToken cancellationToken)
            => Down ? throw new IOException("The store cannot be reached.") : Task.CompletedTask;

        public Task ReopenAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reopens);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeReportsDatabase : IReportsDatabase
    {
        private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _refreshes;
        private long _firstRefreshAt;

        public TimeProvider Time = TimeProvider.System;
        public TimeSpan Latency;

        public int Refreshes => Volatile.Read(ref _refreshes);

        public DateTimeOffset FirstRefreshAt => new(Interlocked.Read(ref _firstRefreshAt), TimeSpan.Zero);

        public Task PingAsync(CancellationToken cancellationToken)
            => Latency > TimeSpan.Zero ? Task.Delay(Latency, Time, cancellationToken) : Task.CompletedTask;

        /// <summary>Lasts until <see cref="FinishRefresh"/>: a refresh takes minutes.</summary>
        public Task RefreshStatisticsAsync(CancellationToken cancellationToken)
        {
            Interlocked.CompareExchange(ref _firstRefreshAt, Time.GetUtcNow().UtcTicks, 0);
            Interlocked.Increment(ref _refreshes);
            return _finish.Task.WaitAsync(cancellationToken);
        }

        public void FinishRefresh() => _finish.TrySetResult();
    }

    private sealed class FakeReportsReader : IReportsForecastReader
    {
        public volatile bool Down;
        private int _resets;

        public int Resets => Volatile.Read(ref _resets);

        public Task PingAsync(CancellationToken cancellationToken)
            => Down ? throw new IOException("The read failed.") : Task.CompletedTask;

        public Task ResetConnectionsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _resets);
            return Task.CompletedTask;
        }
    }
}
