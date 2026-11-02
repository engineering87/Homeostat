using Homeostat;
using Homeostat.Samples.Worker;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<DocumentQueue>();
builder.Services.AddHostedService<DocumentProducer>();
builder.Services.AddHostedService<DocumentIngestWorker>();

builder.Services.AddHomeostat(h => h
    .Configure(o =>
    {
        // Dry-run is on by default. The sample turns it off to show the restart.
        o.DryRun = false;
        o.ApplicationName = "document-ingest-sample";
        o.DetectionInterval = TimeSpan.FromSeconds(2);
        o.DefaultCooldown = TimeSpan.FromSeconds(30);
        o.DefaultVerificationTimeout = TimeSpan.FromSeconds(20);
    })
    // How many documents are waiting, every 2 seconds.
    .AddProbe("ingest-backlog", (sp, _) => Task.FromResult((double)sp.GetRequiredService<DocumentQueue>().Count),
        p => p.UnhealthyAbove = 100)
    // The worker gets stuck now and then: when the backlog stays high, restart it.
    .AddCondition("ingest-stuck", c => c
        .ForSource("ingest-backlog")
        .SustainedFor(TimeSpan.FromSeconds(6))
        .ClearAfter(TimeSpan.FromSeconds(4))
        .Heal(p => p
            .RestartHostedService<DocumentIngestWorker>()
            .CooldownBackoff(2, TimeSpan.FromMinutes(5))))
    .AddAuditLog("logs/homeostat-{date}.jsonl"));

builder.Build().Run();
