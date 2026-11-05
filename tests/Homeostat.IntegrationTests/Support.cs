using System.Collections.Concurrent;
using Homeostat.Detectors;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.IntegrationTests;

/// <summary>Helpers shared by the integration tests.</summary>
internal static class Support
{
    /// <summary>The value of a variable the CI job sets; skips the test when it is missing.</summary>
    public static string Variable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(value), $"Set {name} to run this test against a real server (see the CI workflow).");
        return value!.Trim();
    }

    /// <summary>A unique name, so runs never see each other's queues or topics.</summary>
    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    /// <summary>Waits until <paramref name="condition"/> holds, calling <paramref name="poll"/> in between.</summary>
    public static async Task EventuallyAsync(Func<bool> condition, Func<Task>? poll = null, TimeSpan? timeout = null, string? what = null)
    {
        using var limit = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(60));
        while (!condition())
        {
            if (limit.IsCancellationRequested)
            {
                Assert.Fail($"Timed out waiting for {what ?? "the condition"}.");
            }

            if (poll is not null)
            {
                await poll();
            }

            await Task.Delay(250);
        }
    }

    /// <summary>Runs every detector once, publishing to <paramref name="signals"/>.</summary>
    public static async Task DetectAsync(IServiceProvider services, ISignalPublisher signals)
    {
        foreach (var detector in services.GetServices<ISignalDetector>())
        {
            await detector.DetectAsync(signals, CancellationToken.None);
        }
    }
}

/// <summary>Keeps every signal published to it.</summary>
internal sealed class CollectingPublisher : ISignalPublisher
{
    private readonly ConcurrentQueue<Signal> _signals = new();

    public IReadOnlyList<Signal> Signals => [.. _signals];

    public bool TryPublish(Signal signal)
    {
        _signals.Enqueue(signal);
        return true;
    }

    public Signal? Latest(string source) => Signals.LastOrDefault(s => s.Source == source);
}
