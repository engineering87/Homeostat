using System.Diagnostics.Metrics;
using Homeostat.Detectors;
using Homeostat.Metrics;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Homeostat.Tests;

public class MetricSignalTests
{
    private static (MetricSignalDetector Detector, FakeTimeProvider Time) CreateDetector(params Action<HomeostatBuilder>[] metrics)
    {
        var services = new ServiceCollection();
        var time = new FakeTimeProvider();
        services.AddSingleton<TimeProvider>(time);
        services.AddHomeostat(h =>
        {
            foreach (var metric in metrics)
            {
                metric(h);
            }
        });

        var detector = services.BuildServiceProvider().GetServices<ISignalDetector>().OfType<MetricSignalDetector>().Single();
        return (detector, time);
    }

    private static async Task<IReadOnlyList<Signal>> DetectAsync(MetricSignalDetector detector)
    {
        var sink = new CollectingPublisher();
        await detector.DetectAsync(sink, CancellationToken.None);
        return sink.Signals.ToArray();
    }

    [Fact]
    public async Task Percentile_of_a_histogram_becomes_a_signal_with_its_value()
    {
        using var meter = new Meter($"test.{Guid.NewGuid():N}");
        var histogram = meter.CreateHistogram<double>("forecast.duration");
        var (detector, _) = CreateDetector(h => h.AddMetricSignal("forecast.duration", m => m
            .FromMeter(meter.Name)
            .Source("forecast-latency")
            .Statistic(MetricStatistic.P95)
            .DegradedAbove(1)
            .UnhealthyAbove(3)));
        using var _ = detector;
        await DetectAsync(detector);   // the first interval is only a baseline

        for (var i = 1; i <= 100; i++)
        {
            histogram.Record(i / 50.0);   // 0.02 .. 2.0 seconds
        }

        var signal = Assert.Single(await DetectAsync(detector));
        Assert.Equal("forecast-latency", signal.Source);
        Assert.Equal(SignalSeverity.Degraded, signal.Severity);
        Assert.Equal(1.9, signal.Value!.Value, 2);
        Assert.Equal("100", signal.Tags!["count"]);

        // The interval was reset.
        Assert.Empty(await DetectAsync(detector));
    }

    [Fact]
    public async Task Tag_filters_and_ratios_select_measurements()
    {
        using var meter = new Meter($"test.{Guid.NewGuid():N}");
        var histogram = meter.CreateHistogram<double>("requests");
        var (detector, _) = CreateDetector(h => h.AddMetricSignal("requests", m => m
            .FromMeter(meter.Name)
            .Where("route", "/forecast")
            .RatioOf((_, tags) => tags["status"] is int status && status >= 500)
            .UnhealthyAbove(0.2)
            .MinimumCount(5)));
        using var _ = detector;
        await DetectAsync(detector);   // the first interval is only a baseline

        for (var i = 0; i < 10; i++)
        {
            histogram.Record(0.1, new KeyValuePair<string, object?>("route", "/forecast"), new KeyValuePair<string, object?>("status", i < 3 ? 503 : 200));
            histogram.Record(0.1, new KeyValuePair<string, object?>("route", "/other"), new KeyValuePair<string, object?>("status", 500));
        }

        var signal = Assert.Single(await DetectAsync(detector));
        Assert.Equal(0.3, signal.Value!.Value, 3);
        Assert.Equal(SignalSeverity.Unhealthy, signal.Severity);
    }

    [Fact]
    public async Task Counters_rates_gauges_and_minimum_count()
    {
        using var meter = new Meter($"test.{Guid.NewGuid():N}");
        var counter = meter.CreateCounter<long>("jobs.failed");
        var queue = 42;
        meter.CreateObservableGauge("queue.length", () => queue);
        var (detector, time) = CreateDetector(
            h => h.AddMetricSignal("jobs.failed", m => m.FromMeter(meter.Name).Statistic(MetricStatistic.Rate).UnhealthyAbove(1)),
            h => h.AddMetricSignal("queue.length", m => m.FromMeter(meter.Name).Statistic(MetricStatistic.Last).DegradedAbove(100)),
            h => h.AddMetricSignal("jobs.failed", m => m.FromMeter(meter.Name).Source("rare").Statistic(MetricStatistic.Count).UnhealthyAbove(1).MinimumCount(1000)));
        using var _ = detector;
        await DetectAsync(detector);   // the first interval is only a baseline

        counter.Add(20);
        counter.Add(10);
        time.Advance(TimeSpan.FromSeconds(10));

        var signals = await DetectAsync(detector);
        var rate = signals.Single(s => s.Source == "metric:jobs.failed");
        Assert.Equal(3, rate.Value!.Value, 3);
        Assert.Equal(SignalSeverity.Unhealthy, rate.Severity);

        var gauge = signals.Single(s => s.Source == "metric:queue.length");
        Assert.Equal(42, gauge.Value);
        Assert.Equal(SignalSeverity.Healthy, gauge.Severity);

        Assert.DoesNotContain(signals, s => s.Source == "rare");
    }

    [Fact]
    public async Task Up_down_counters_report_their_level_and_observable_counters_their_increments()
    {
        using var meter = new Meter($"test.{Guid.NewGuid():N}");
        var active = meter.CreateUpDownCounter<long>("requests.active");
        long total = 100;
        meter.CreateObservableCounter("jobs.done", () => total);
        var (detector, time) = CreateDetector(
            h => h.AddMetricSignal("requests.active", m => m.FromMeter(meter.Name).Statistic(MetricStatistic.Last).DegradedAbove(4)),
            h => h.AddMetricSignal("jobs.done", m => m.FromMeter(meter.Name).Statistic(MetricStatistic.Rate).UnhealthyBelow(0.5)));
        using var _ = detector;
        await DetectAsync(detector);   // baseline: the observable counter starts at 100

        active.Add(5);
        active.Add(-1);
        total += 30;
        time.Advance(TimeSpan.FromSeconds(10));
        var signals = await DetectAsync(detector);

        Assert.Equal(4, signals.Single(s => s.Source == "metric:requests.active").Value);
        Assert.Equal(3, signals.Single(s => s.Source == "metric:jobs.done").Value!.Value, 3);

        time.Advance(TimeSpan.FromSeconds(10));
        signals = await DetectAsync(detector);
        var stalled = signals.Single(s => s.Source == "metric:jobs.done");
        Assert.Equal(0, stalled.Value);
        Assert.Equal(SignalSeverity.Unhealthy, stalled.Severity);
    }

    [Fact]
    public async Task A_throwing_filter_never_breaks_the_instrumented_code()
    {
        using var meter = new Meter($"test.{Guid.NewGuid():N}");
        var counter = meter.CreateCounter<long>("calls");
        var (detector, _) = CreateDetector(
            h => h.AddMetricSignal("calls", m => m.FromMeter(meter.Name).Source("broken").Where(tags => (int)tags["code"]! > 0).UnhealthyAbove(1)),
            h => h.AddMetricSignal("calls", m => m.FromMeter(meter.Name).Source("fine").Statistic(MetricStatistic.Count).UnhealthyAbove(100)));
        using var _ = detector;
        await DetectAsync(detector);

        counter.Add(1, new KeyValuePair<string, object?>("code", 5L));   // a long, not an int: the cast throws

        var fine = Assert.Single(await DetectAsync(detector));
        Assert.Equal("fine", fine.Source);
        Assert.Equal(1, fine.Value);
    }

    [Fact]
    public void Metric_signals_need_a_threshold_and_a_ratio_rule()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHomeostat(h => h.AddMetricSignal("x", _ => { })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHomeostat(h => h.AddMetricSignal("x", m => m.Statistic(MetricStatistic.Ratio).UnhealthyAbove(1))));
    }

    [Fact]
    public async Task Server_error_rate_preset_reads_the_aspnetcore_metric()
    {
        // Same meter and instrument names as ASP.NET Core; other tests may add a few real requests, hence the tolerance.
        using var meter = new Meter("Microsoft.AspNetCore.Hosting");
        var duration = meter.CreateHistogram<double>("http.server.request.duration");
        var (detector, _) = CreateDetector(h => h.AddServerErrorRateSignal(o => o.MinimumRequests = 100));
        using var _ = detector;
        await DetectAsync(detector);   // the first interval is only a baseline

        for (var i = 0; i < 1000; i++)
        {
            duration.Record(0.05, new KeyValuePair<string, object?>("http.response.status_code", i % 10 == 0 ? 503 : 200));
        }

        var signal = Assert.Single(await DetectAsync(detector), s => s.Source == "http-server:errors");
        Assert.InRange(signal.Value!.Value, 0.09, 0.11);
        Assert.Equal(SignalSeverity.Unhealthy, signal.Severity);
    }
}
