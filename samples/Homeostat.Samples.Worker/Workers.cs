using Homeostat;
using System.Collections.Concurrent;

namespace Homeostat.Samples.Worker;

/// <summary>Documents waiting to be ingested.</summary>
public sealed class DocumentQueue
{
    private readonly ConcurrentQueue<int> _items = new();

    public int Count => _items.Count;

    public void Enqueue(int document) => _items.Enqueue(document);

    public bool TryDequeue(out int document) => _items.TryDequeue(out document);
}

/// <summary>Adds 20 documents per second, like an upstream system would.</summary>
public sealed class DocumentProducer(DocumentQueue queue) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var next = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            queue.Enqueue(next++);
        }
    }
}

/// <summary>
/// Ingests documents faster than they arrive, but gets stuck after a while (as if a connection hung without a
/// timeout). Only a restart unblocks it: that is what Homeostat does.
/// </summary>
/// <remarks>
/// It derives from <see cref="RestartableBackgroundService"/>, so Homeostat restarts it without the host noticing.
/// </remarks>
public sealed class DocumentIngestWorker(DocumentQueue queue, ILogger<DocumentIngestWorker> logger) : RestartableBackgroundService
{
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Document ingestion started.");
        var processed = 0;
        var stuckAfter = Random.Shared.Next(300, 600);
        while (!stoppingToken.IsCancellationRequested)
        {
            while (queue.TryDequeue(out _))
            {
                if (++processed == stuckAfter)
                {
                    logger.LogWarning("Ingestion is stuck after {Processed} documents (simulated hang).", processed);
                    await Task.Delay(Timeout.Infinite, stoppingToken);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
        }
    }
}
