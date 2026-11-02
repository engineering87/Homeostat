// Measures what Homeostat costs the application: publishing a signal, processing signals in the engine, and the
// HTTP client handler. No external benchmark library: each case runs a warm-up, then several measured runs, and the
// median is reported. Results are indicative; compare runs on the same machine.
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Homeostat;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var quick = args.Contains("--quick");
var runs = quick ? 3 : 7;
var rows = new List<string>();

Console.WriteLine($"# Homeostat overhead, {DateTimeOffset.UtcNow:yyyy-MM-dd}");
Console.WriteLine();
Console.WriteLine($"- Runtime: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}");
Console.WriteLine($"- Processor: {Processor()}, {Environment.ProcessorCount} logical cores");
Console.WriteLine($"- Workstation GC, non-concurrent; median of {runs} runs after a warm-up");
Console.WriteLine();

// 1. Publishing: the cost on the caller's thread, with the engine running and draining the buffer.
using (var host = await StartAsync(1, rule: null))
{
    var publisher = host.Services.GetRequiredService<ISignalPublisher>();
    var signal = new Signal("source-0", SignalSeverity.Healthy, DateTimeOffset.UtcNow);
    rows.Add(Row("TryPublish(signal)", PerOperation(1_000_000, runs, () => publisher.TryPublish(signal))));
    rows.Add(Row("TryPublish(source, severity)", PerOperation(1_000_000, runs, () => publisher.TryPublish("source-0", SignalSeverity.Healthy))));
}

// 2. Engine: signals processed per second, by number and kind of conditions.
foreach (var (conditions, rule, label) in new (int, Action<Homeostat.Conditions.ConditionBuilder>?, string)[]
{
    (1, null, "1 condition, severity rule"),
    (10, null, "10 conditions, severity rule"),
    (100, null, "100 conditions, severity rule"),
    (100, c => c.WhenValueAbove(1000), "100 conditions, value threshold"),
    (100, c => c.WhenRisingFasterThan(10, TimeSpan.FromMinutes(5)), "100 conditions, trend over 5 min"),
    (100, c => c.WhenAnomalouslyHigh(4, TimeSpan.FromMinutes(5)), "100 conditions, anomaly over 5 min"),
})
{
    rows.Add(await EngineRowAsync(label, conditions, rule, quick ? 50_000 : 200_000, runs));
}

// 3. HTTP client handler: one request through a client with and without AddHomeostatSignals, to a stub handler.
// The first client measured in a process is slower whichever it is (the HTTP stack warms up): one pass is discarded.
await HttpPerOperationAsync(signals: true, quick ? 100_000 : 500_000, runs: 1);
rows.Add(Row("HttpClient.SendAsync, no Homeostat", await HttpPerOperationAsync(signals: false, quick ? 100_000 : 500_000, runs)));
rows.Add(Row("HttpClient.SendAsync, AddHomeostatSignals()", await HttpPerOperationAsync(signals: true, quick ? 100_000 : 500_000, runs)));

Console.WriteLine("| Case | Time | Allocated |");
Console.WriteLine("|---|---:|---:|");
rows.ForEach(Console.WriteLine);
return 0;

static string Row(string name, (double Nanoseconds, double Bytes) result)
    => $"| {name} | {result.Nanoseconds.ToString("0.0", CultureInfo.InvariantCulture)} ns/op | {result.Bytes.ToString("0", CultureInfo.InvariantCulture)} B/op |";

static (double Nanoseconds, double Bytes) PerOperation(int operations, int runs, Func<bool> operation)
{
    Run(operations);
    var results = Enumerable.Range(0, runs).Select(_ => Run(operations)).ToArray();
    return (Median(results.Select(r => r.Nanoseconds)), Median(results.Select(r => r.Bytes)));

    (double Nanoseconds, double Bytes) Run(int count)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            operation();
        }

        watch.Stop();
        return (watch.Elapsed.TotalNanoseconds / count, (double)(GC.GetAllocatedBytesForCurrentThread() - allocated) / count);
    }
}

static async Task<string> EngineRowAsync(string label, int conditions, Action<Homeostat.Conditions.ConditionBuilder>? rule, int signals, int runs)
{
    var marker = new MarkerObserver();
    using var host = await StartAsync(conditions, rule, capacity: signals + 16, marker);
    var publisher = host.Services.GetRequiredService<ISignalPublisher>();
    var next = DateTimeOffset.UtcNow;

    await RunAsync(signals / 10);
    var results = new List<(double PerSecond, double Bytes)>();
    for (var run = 0; run < runs; run++)
    {
        results.Add(await RunAsync(signals));
    }

    var perSecond = Median(results.Select(r => r.PerSecond));
    var bytes = Median(results.Select(r => r.Bytes));
    return $"| Engine, {label} | {(1e9 / perSecond).ToString("0", CultureInfo.InvariantCulture)} ns/signal ({(perSecond / 1e6).ToString("0.00", CultureInfo.InvariantCulture)} M signals/s) | {bytes.ToString("0", CultureInfo.InvariantCulture)} B/signal |";

    async Task<(double PerSecond, double Bytes)> RunAsync(int count)
    {
        // A last, unhealthy signal activates the marker condition: once the engine has handled it, it has handled
        // the ones before (signals are processed in order). Waiting costs one notification, not polling.
        // Values stay low and flat: the conditions are evaluated but never activate, as in normal operation. Time moves
        // on by one millisecond per signal, across runs too, so windows hold what they would in production.
        var batch = new Signal[count];
        for (var i = 0; i < count; i++)
        {
            batch[i] = new Signal($"source-{i % conditions}", SignalSeverity.Healthy, next) { Value = 10 + (i % 7) };
            next = next.AddMilliseconds(1);
        }

        var activated = marker.Expect(activation: true);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            publisher.TryPublish(batch[i]);
        }

        publisher.TryPublish(new Signal("marker", SignalSeverity.Unhealthy, batch[count - 1].Timestamp));
        await activated;
        watch.Stop();
        var bytes = (double)(GC.GetTotalAllocatedBytes(precise: true) - allocated) / count;

        var cleared = marker.Expect(activation: false);
        publisher.TryPublish(new Signal("marker", SignalSeverity.Healthy, batch[count - 1].Timestamp));
        await cleared;
        return (count / watch.Elapsed.TotalSeconds, bytes);
    }
}

static async Task<(double Nanoseconds, double Bytes)> HttpPerOperationAsync(bool signals, int operations, int runs)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Services.AddHomeostat(h => h.AddCondition("feed-errors", c => c.ForSource("http-client:feed")));
    var client = builder.Services.AddHttpClient("feed", c => c.BaseAddress = new Uri("http://feed.invalid/"))
        .ConfigurePrimaryHttpMessageHandler(() => new StubHandler());
    if (signals)
    {
        client.AddHomeostatSignals();
    }

    using var host = builder.Build();
    await host.StartAsync();
    var http = host.Services.GetRequiredService<IHttpClientFactory>().CreateClient("feed");

    await RunAsync(operations);
    var results = new List<(double, double)>();
    for (var run = 0; run < runs; run++)
    {
        results.Add(await RunAsync(operations));
    }

    await host.StopAsync();
    return (Median(results.Select(r => r.Item1)), Median(results.Select(r => r.Item2)));

    async Task<(double, double)> RunAsync(int count)
    {
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, "forecast");
            using var response = await http.SendAsync(message);
        }

        watch.Stop();
        return (watch.Elapsed.TotalNanoseconds / count, (double)(GC.GetTotalAllocatedBytes(precise: true) - allocated) / count);
    }
}

static async Task<IHost> StartAsync(int conditions, Action<Homeostat.Conditions.ConditionBuilder>? rule, int capacity = 1024, MarkerObserver? marker = null)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    if (marker is not null)
    {
        builder.Services.AddSingleton<IHealingObserver>(marker);
    }

    builder.Services.AddHomeostat(h =>
    {
        h.Configure(o => o.SignalBufferCapacity = capacity);
        h.AddCondition("marker", c => c.ForSource("marker"));
        for (var i = 0; i < conditions; i++)
        {
            var source = $"source-{i}";
            h.AddCondition($"condition-{i}", c =>
            {
                c.ForSource(source);
                rule?.Invoke(c);
            });
        }
    });

    var host = builder.Build();
    await host.StartAsync();
    return host;
}

static double Median(IEnumerable<double> values)
{
    var sorted = values.Order().ToArray();
    return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
}

static string Processor()
{
    try
    {
        var line = File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
        if (line is not null)
        {
            return line[(line.IndexOf(':') + 1)..].Trim();
        }
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }

    return RuntimeInformation.ProcessArchitecture.ToString();
}

/// <summary>Answers every request with 200 OK at once: only the client pipeline is measured.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
}

/// <summary>Completes a task when the marker condition activates or clears.</summary>
internal sealed class MarkerObserver : IHealingObserver
{
    private TaskCompletionSource? _activated;
    private TaskCompletionSource? _cleared;

    public Task Expect(bool activation)
    {
        var expected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (activation)
        {
            _activated = expected;
        }
        else
        {
            _cleared = expected;
        }

        return expected.Task;
    }

    public void OnConditionActivated(Homeostat.Conditions.ConditionSnapshot condition)
    {
        if (condition.Name == "marker")
        {
            _activated?.TrySetResult();
        }
    }

    public void OnConditionCleared(Homeostat.Conditions.ConditionSnapshot condition)
    {
        if (condition.Name == "marker")
        {
            _cleared?.TrySetResult();
        }
    }
}
