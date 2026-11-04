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
        // Cooldowns, budget and history survive restarts.
        .UseFileStore("data/homeostat.json")
        // Built-in detectors: no code needed to watch the runtime.
        .AddThreadPoolStarvationDetector()
        .AddMemoryPressureDetector()
        // Share of 5xx answers, from ASP.NET Core's own metrics.
        .AddServerErrorRateSignal()
        .AddCondition("api-errors", c => c
            .ForSource("http-server:errors")
            .SustainedFor(TimeSpan.FromMinutes(1)))
        // A slow memory leak shows up as a trend long before the memory is full.
        .AddCondition("memory-leak-suspected", c => c
            .ForSource("memory")
            .WhenRisingFasterThan(0.005, TimeSpan.FromMinutes(15)))
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
                .Then<SwitchToBackupProviderAction>().RequireApproval(TimeSpan.FromMinutes(5))
                // Retry less and less often while the feed stays broken.
                .CooldownBackoff(2, TimeSpan.FromMinutes(5))))
        // Fed by your monitoring through POST /homeostat/alerts/alertmanager: heal what alerts already detect.
        .AddCondition("radar-tiles-slow", c => c
            .ForSource("alert:RadarTilesSlow")
            .WhenSeverityAtLeast(SignalSeverity.Degraded)
            .Heal(p => p.TurnOnSwitch("hide-radar-tiles", turnOffWhenCleared: true)))
        .AddHealthCheckSignals(o => o.Interval = TimeSpan.FromSeconds(2))
        // Every decision, one JSON object per line.
        .AddAuditLog("logs/homeostat-{date}.jsonl"));

var app = builder.Build();

app.MapHealthChecks("/health");

// GET /homeostat/status: conditions, runs, approvals, switches, budget.
// POST /homeostat/pause, /resume, /approvals/{id}/approve|reject, /switches/{name}/on|off.
// Demo only: a real application passes an authorization policy, for example MapHomeostatControl("operations").
app.MapHomeostatStatus(allowAnonymous: true);
app.MapHomeostatControl(allowAnonymous: true);
// GET /homeostat/dashboard: the same information, with one-click approvals.
app.MapHomeostatDashboard(allowAnonymous: true);
// POST /homeostat/alerts/alertmanager | grafana | azure-monitor | signals. A real application sets a token or a policy.
app.MapHomeostatAlerts(allowAnonymous: true);

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
