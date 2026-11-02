using Homeostat;
using HomeostatWebApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ReferenceDataClient>();
builder.Services.AddHealthChecks()
    .AddCheck<ReferenceDataHealthCheck>("reference-data")
    .AddHomeostatCheck();

builder.Services.AddHomeostat(h => h
    // Dry-run logs every decision without acting. Watch the logs, then set DryRun = false (here or in appsettings.json).
    .Configure(o => o.DryRun = builder.Configuration.GetValue("Homeostat:DryRun", true))
    // Signals: health checks and the runtime.
    .AddHealthCheckSignals()
    .AddThreadPoolStarvationDetector()
    // When the reference data provider fails for 30 seconds: renew the client, then serve the cached copy.
    .AddCondition("reference-data-down", c => c
        .ForSource("reference-data")
        .SustainedFor(TimeSpan.FromSeconds(30))
        .ClearAfter(TimeSpan.FromMinutes(1))
        .SuppressedBy("threadpool-starved")
        .Heal(p => p
            .With("renew-client", sp => sp.GetRequiredService<ReferenceDataClient>().Renew())
            .TurnOnSwitch("serve-cached-reference-data", turnOffWhenCleared: true)))
    // Observe only: tracked and shown in the dashboard, never acted upon.
    .AddCondition("threadpool-starved", c => c
        .ForSource("threadpool")
        .SustainedFor(TimeSpan.FromSeconds(30)))
    .AddAuditLog("logs/homeostat-{date}.jsonl"));

// The Homeostat endpoints reveal internals and change behavior: they need an authorization policy.
// Configure authentication (JWT bearer, cookies, ...) and give operators the "operations" role.
builder.Services.AddAuthorization(o => o.AddPolicy("operations", p => p.RequireRole("operations")));

var app = builder.Build();

app.UseAuthorization();
app.MapHealthChecks("/health");

if (app.Environment.IsDevelopment())
{
    // Local development only: no authorization, so the dashboard opens at /homeostat/dashboard.
    app.MapHomeostatStatus(allowAnonymous: true);
    app.MapHomeostatControl(allowAnonymous: true);
    app.MapHomeostatDashboard(allowAnonymous: true);
}
else
{
    app.MapHomeostatStatus("operations");
    app.MapHomeostatControl("operations");
    app.MapHomeostatDashboard("operations");
}

app.MapGet("/regions", async (ReferenceDataClient client, IHomeostatSwitches switches, CancellationToken ct) =>
    switches.IsOn("serve-cached-reference-data")
        ? Results.Ok(new { source = "cache", regions = client.CachedRegions })
        : Results.Ok(new { source = "provider", regions = await client.GetRegionsAsync(ct) }));

app.Run();
