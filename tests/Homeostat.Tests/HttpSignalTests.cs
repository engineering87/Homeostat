using Homeostat.Detectors;
using Homeostat.Http;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Tests;

public class HttpSignalTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly HttpClientSignalOptions Defaults = new();

    [Fact]
    public void No_requests_produce_no_signal()
        => Assert.Null(HttpClientSignalDetector.Evaluate("http:x", Defaults, new HttpWindowSample(0, 0, null), Now));

    [Fact]
    public void Few_clean_requests_are_healthy()
        => Assert.Equal(
            SignalSeverity.Healthy,
            HttpClientSignalDetector.Evaluate("http:x", Defaults, new HttpWindowSample(3, 0, null), Now)!.Severity);

    [Fact]
    public void Few_requests_with_failures_produce_no_signal()
        => Assert.Null(HttpClientSignalDetector.Evaluate("http:x", Defaults, new HttpWindowSample(3, 1, null), Now));

    [Theory]
    [InlineData(100, 5, SignalSeverity.Healthy)]
    [InlineData(100, 20, SignalSeverity.Degraded)]
    [InlineData(100, 60, SignalSeverity.Unhealthy)]
    public void Failure_ratio_sets_the_severity(int total, int failures, SignalSeverity expected)
        => Assert.Equal(
            expected,
            HttpClientSignalDetector.Evaluate("http:x", Defaults, new HttpWindowSample(total, failures, null), Now)!.Severity);

    [Fact]
    public void Slow_p95_raises_the_severity()
    {
        var options = new HttpClientSignalOptions
        {
            DegradedLatency = TimeSpan.FromMilliseconds(500),
            UnhealthyLatency = TimeSpan.FromSeconds(2),
        };

        Assert.Equal(SignalSeverity.Degraded,
            HttpClientSignalDetector.Evaluate("http:x", options, new HttpWindowSample(50, 0, TimeSpan.FromMilliseconds(800)), Now)!.Severity);
        Assert.Equal(SignalSeverity.Unhealthy,
            HttpClientSignalDetector.Evaluate("http:x", options, new HttpWindowSample(50, 0, TimeSpan.FromSeconds(3)), Now)!.Severity);
    }

    [Fact]
    public void Percentile_95_picks_the_right_sample()
    {
        var samples = Enumerable.Range(1, 100).Select(i => (double)i).Reverse().ToArray();
        Assert.Equal(TimeSpan.FromMilliseconds(95), HttpClientWindow.Percentile95(samples));
    }

    [Fact]
    public void Signal_settings_that_could_never_work_are_refused()
    {
        static void Add(Action<HttpClientSignalOptions> configure)
            => new ServiceCollection().AddHttpClient("forecast").AddHomeostatSignals(configure);

        Assert.Throws<ArgumentOutOfRangeException>(() => Add(o => o.DegradedFailureRatio = 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Add(o => o.UnhealthyFailureRatio = double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Add(o => o.DegradedFailureRatio = 0.6));   // above the unhealthy 0.5
        Assert.Throws<ArgumentOutOfRangeException>(() => Add(o => o.UnhealthyLatency = TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => Add(o => o.MinimumCalls = 0));
        Assert.Throws<ArgumentException>(() => Add(o => o.IsFailure = null!));
    }

    [Fact]
    public async Task Instrumented_client_reports_failures()
    {
        var fault = new FaultSwitch();
        fault.Activate();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHomeostat();
        services.AddHttpClient("forecast")
            .ConfigurePrimaryHttpMessageHandler(() => new FaultInjectingHandler(fault))
            .AddHomeostatSignals()
            .AddHomeostatSignals(o => o.MinimumCalls = 5);   // a second call must not count requests twice

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("forecast");

        for (var i = 0; i < 10; i++)
        {
            using var response = await client.GetAsync("http://forecast.test/today");
        }

        var detector = provider.GetServices<ISignalDetector>().OfType<HttpClientSignalDetector>().Single();
        var sink = new CollectingPublisher();
        await detector.DetectAsync(sink, CancellationToken.None);

        var signal = Assert.Single(sink.Signals);
        Assert.Equal("http:forecast", signal.Source);
        Assert.Equal(SignalSeverity.Unhealthy, signal.Severity);
        Assert.Equal("10", signal.Tags!["failures"]);
    }
}
