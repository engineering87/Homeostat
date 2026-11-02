using Homeostat;
using System.Collections.Concurrent;

namespace HomeostatWorker;

/// <summary>Documents waiting to be ingested. Replace it with your queue or broker client.</summary>
public sealed class DocumentQueue
{
    private readonly ConcurrentQueue<string> _items = new();

    public int Count => _items.Count;

    public void Enqueue(string document) => _items.Enqueue(document);

    public bool TryDequeue(out string? document) => _items.TryDequeue(out document);
}

/// <summary>
/// Ingests documents. It derives from RestartableBackgroundService, so Homeostat can restart it when the backlog shows
/// it is stuck, or when its loop crashed, without the host noticing.
/// </summary>
public sealed class DocumentIngestWorker(DocumentQueue queue, ILogger<DocumentIngestWorker> logger) : RestartableBackgroundService
{
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Document ingestion started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            while (queue.TryDequeue(out var document))
            {
                logger.LogDebug("Ingested {Document}.", document);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
