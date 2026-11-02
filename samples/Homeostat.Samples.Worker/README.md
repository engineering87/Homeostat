# Worker sample

A background worker that ingests documents and gets stuck now and then, as if a connection hung without a timeout.
Homeostat watches the backlog with a probe and restarts the worker when the backlog stays high.

```bash
dotnet run --project samples/Homeostat.Samples.Worker
```

Watch the logs: after a few seconds the worker logs that it is stuck, the `ingest-backlog` probe reports more than
100 waiting documents, the `ingest-stuck` condition becomes active, and Homeostat restarts `DocumentIngestWorker`.
The backlog drains and the condition clears. Every decision is also in `logs/homeostat-*.jsonl`.

| Piece | Where |
|---|---|
| Probe on the backlog | `AddProbe("ingest-backlog", ...)` in `Program.cs` |
| Condition and plan | `AddCondition("ingest-stuck", ...)` with `RestartHostedService<DocumentIngestWorker>()` |
| The simulated hang | `DocumentIngestWorker` in `Workers.cs` |

The sample turns dry-run off to show the restart. In a real application, start with dry-run and read the logs first.
