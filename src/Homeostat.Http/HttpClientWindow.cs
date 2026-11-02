namespace Homeostat.Http;

internal readonly record struct HttpWindowSample(int Total, int Failures, TimeSpan? P95Latency);

/// <summary>Collects request outcomes for one client between two detector samples.</summary>
internal sealed class HttpClientWindow
{
    private const int MaxLatencySamples = 2048;

    private readonly object _gate = new();
    private readonly List<double> _latenciesMs = [];
    private int _total;
    private int _failures;

    public HttpClientWindow(string clientName, HttpClientSignalOptions options)
    {
        ClientName = clientName;
        Options = options;
        Source = string.IsNullOrWhiteSpace(options.Source) ? $"http:{clientName}" : options.Source;
    }

    public string ClientName { get; }

    public string Source { get; }

    public HttpClientSignalOptions Options { get; }

    public void Record(bool failed, TimeSpan latency)
    {
        lock (_gate)
        {
            _total++;
            if (failed)
            {
                _failures++;
            }

            if (_latenciesMs.Count < MaxLatencySamples)
            {
                _latenciesMs.Add(latency.TotalMilliseconds);
            }
            else
            {
                // Reservoir sampling: every request of the window has the same chance to be kept,
                // so the p95 reflects the whole interval and not only its beginning.
                var slot = Random.Shared.NextInt64(_total);
                if (slot < MaxLatencySamples)
                {
                    _latenciesMs[(int)slot] = latency.TotalMilliseconds;
                }
            }
        }
    }

    /// <summary>Returns the sample collected since the previous call and starts a new one.</summary>
    public HttpWindowSample Collect()
    {
        double[] latencies;
        int total;
        int failures;

        lock (_gate)
        {
            total = _total;
            failures = _failures;
            latencies = _latenciesMs.ToArray();
            _total = 0;
            _failures = 0;
            _latenciesMs.Clear();
        }

        return new HttpWindowSample(total, failures, Percentile95(latencies));
    }

    internal static TimeSpan? Percentile95(double[] latenciesMs)
    {
        if (latenciesMs.Length == 0)
        {
            return null;
        }

        Array.Sort(latenciesMs);
        var index = (int)Math.Ceiling(0.95 * latenciesMs.Length) - 1;
        return TimeSpan.FromMilliseconds(latenciesMs[Math.Clamp(index, 0, latenciesMs.Length - 1)]);
    }
}
