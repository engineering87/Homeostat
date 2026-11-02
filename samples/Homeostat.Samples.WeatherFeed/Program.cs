using Homeostat;
using Homeostat.Samples.WeatherFeed;
using Homeostat.Signals;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<WeatherFeedClient>();
builder.Services.AddHealthChecks()
    .AddCheck<WeatherFeedHealthCheck>("weather-feed")
    // Homeostat's own state (paused, budget, pending approvals, active conditions) in /health.
    .AddHomeostatCheck();

builder.Services
    .AddHomeostat(h => h
        .Configure(o =>
        {
            // Dry-run is on by default. The sample turns it off to show real remediation.
            o.DryRun = false;
            o.ApplicationName = "weather-feed-sample";
            o.DefaultCooldown = TimeSpan.FromSeconds(20);
            o.DefaultVerificationTimeout = TimeSpan.FromSeconds(10);
            o.DetectionInterval = TimeSpan.FromSeconds(5);
        })
        // Built-in detectors: no code needed to watch the runtime.
        .AddThreadPoolStarvationDetector()
        .AddMemoryPressureDetector()
        // Observe only: no Heal(...), so it is tracked and logged but never acts.
        .AddCondition("threadpool-starved", c => c
            .ForSource("threadpool")
            .SustainedFor(TimeSpan.FromSeconds(15)))
        .AddCondition("weather-feed-down", c => c
            .ForSource("weather-feed")
            .WhenSeverityAtLeast(SignalSeverity.Unhealthy)
            .SustainedFor(TimeSpan.FromSeconds(4))
            // Recovery must hold for 6 seconds before the condition clears: no flapping.
            .ClearAfter(TimeSpan.FromSeconds(6))
            // A starved thread pool makes every call time out: renewing the client would not help.
            .SuppressedBy("threadpool-starved")
            .Heal(p => p
                .With<RenewFeedClientAction>()
                // Failing over changes the data users see: a person approves it.
                .Then<SwitchToBackupProviderAction>().RequireApproval(TimeSpan.FromMinutes(5))))
        .AddHealthCheckSignals(period: TimeSpan.FromSeconds(2))
        // Every decision, one JSON object per line.
        .AddAuditLog("logs/homeostat-{date}.jsonl"));

var app = builder.Build();

app.MapHealthChecks("/health");

// GET /homeostat/status: conditions, runs, approvals, switches, budget.
// POST /homeostat/pause, /resume, /approvals/{id}/approve|reject, /switches/{name}/on|off.
// Demo only: a real application passes an authorization policy, for example MapHomeostatControl("operations").
app.MapHomeostatStatus(allowAnonymous: true);
app.MapHomeostatControl(allowAnonymous: true);

app.MapGet("/feed", (WeatherFeedClient client) => new { client.IsStuck, client.ActiveProvider, client.Renewals });

// A fault that renewing the client fixes.
app.MapPost("/faults/stuck-client", (WeatherFeedClient client) =>
{
    client.SimulateStuckClient();
    return Results.Accepted();
});

// A fault that renewing cannot fix: Homeostat asks for approval to switch to the backup provider.
app.MapPost("/faults/primary-outage", (WeatherFeedClient client) =>
{
    client.SimulatePrimaryOutage();
    return Results.Accepted();
});

app.MapPost("/restore", (WeatherFeedClient client) =>
{
    client.Restore();
    return Results.Ok();
});

app.Run();
