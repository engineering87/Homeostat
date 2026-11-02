using Homeostat;
using HomeostatWorker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<DocumentQueue>();
builder.Services.AddHostedService<DocumentIngestWorker>();

builder.Services.AddHomeostat(h => h
    // Dry-run logs every decision without acting. Watch the logs, then set Homeostat:DryRun to false.
    .Configure(o => o.DryRun = builder.Configuration.GetValue("Homeostat:DryRun", true))
    .AddMemoryPressureDetector()
    // Healthy while the worker runs, Unhealthy if its loop crashed.
    .AddBackgroundServiceSignals()
    // The number of documents waiting, every 15 seconds.
    .AddProbe("ingest-backlog", (sp, _) => Task.FromResult((double)sp.GetRequiredService<DocumentQueue>().Count),
        p => { p.Interval = TimeSpan.FromSeconds(15); p.UnhealthyAbove = 500; })
    // A backlog that keeps growing means the worker is stuck: restart it.
    .AddCondition("ingest-stuck", c => c
        .ForSource("ingest-backlog")
        .SustainedFor(TimeSpan.FromMinutes(1))
        .Heal(p => p
            .RestartHostedService<DocumentIngestWorker>()
            .CooldownBackoff(2, TimeSpan.FromMinutes(30))))
    // A crashed loop: restart it at once.
    .AddCondition("ingest-crashed", c => c
        .ForSource("service:DocumentIngestWorker")
        .Heal(p => p.RestartHostedService<DocumentIngestWorker>()))
    .AddAuditLog("logs/homeostat-{date}.jsonl"));

builder.Build().Run();
