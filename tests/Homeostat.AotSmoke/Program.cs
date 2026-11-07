// Native AOT smoke test: a trimmed, ahead-of-time compiled application that uses the main Homeostat features end to
// end (signals, a condition, a healing plan, the file store, the audit log, signal recording, notifications and the
// HTTP endpoints) and checks the results. Exit code 0: everything worked without reflection or runtime code generation.
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Homeostat;
using Homeostat.Conditions;
using Homeostat.Http;
using Homeostat.Signals;

var directory = Directory.CreateTempSubdirectory("homeostat-aot-");
var healed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var sent = new ConcurrentQueue<(string Client, string Body)>();

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddMemoryCache();
builder.Services.AddHealthChecks().AddHomeostatCheck();
builder.Services.AddHomeostat(h => h
    .Configure(o =>
    {
        o.DryRun = false;
        o.ApplicationName = "aot-smoke";
        o.DetectionInterval = TimeSpan.FromSeconds(1);
    })
    .UseFileStore(Path.Combine(directory.FullName, "homeostat.json"))
    .AddAuditLog(Path.Combine(directory.FullName, "audit-{date}.jsonl"))
    .AddSignalRecording(o => o.Path = Path.Combine(directory.FullName, "signals-{instance}-{date}.jsonl"))
    .AddCondition("feed-down", c => c
        .ForSource("alert:feed") // The alert endpoints prefix the sources they receive.
        .WhenSeverityAtLeast(SignalSeverity.Unhealthy)
        .Heal(p => p.With("renew-feed-client", _ => healed.TrySetResult())))
    .AddCondition("feed-latency-unusual", c => c
        .ForSource("feed-latency")
        .WhenSeasonallyHigh(3, Seasonality.Daily()))
    // Never activated here: registering these plans compiles the cache, Kubernetes and Azure actions into the
    // application, so the Native AOT compiler analyzes them too.
    .AddCondition("never-activated", c => c
        .ForSource("unused")
        .Heal(p => p
            .CompactMemoryCache()
            .RolloutRestart("weather-api")
            .RestartAppService("/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/smoke/providers/Microsoft.Web/sites/weather-api")))
    .UseKubernetes(o =>
    {
        o.ApiServer = new Uri("https://kubernetes.invalid");
        o.Namespace = "smoke";
        o.AccessToken = (_, _) => ValueTask.FromResult("token");
    })
    .UseAzure(o => o.AccessToken = (_, _) => ValueTask.FromResult("token"))
    .AddCallSignals("geocoding:api")
    .AddWebhookNotifications(o =>
    {
        o.Url = new Uri("https://receiver.invalid/homeostat");
        o.CreatePayload = e => new JsonObject { ["text"] = $"[{e.Application}] {e.Summary}", ["kind"] = e.Kind.ToString() };
    })
    .AddPagerDutyIncidents(o =>
    {
        o.RoutingKey = "smoke-routing-key";
        o.OnConditionActivated = true;
    })
    .AddOpsgenieIncidents(o =>
    {
        o.ApiKey = "smoke-api-key";
        o.OnConditionActivated = true;
    })
    .AddSlackNotifications(o => o.Url = new Uri("https://hooks.slack.com/services/smoke"))
    .AddTeamsNotifications(o => o.Url = new Uri("https://teams.invalid/workflow")));

// The notifiers send to an in-memory receiver.
foreach (var client in new[] { WebhookOptions.HttpClientName, "Homeostat.Slack", "Homeostat.Teams", "Homeostat.PagerDuty", "Homeostat.Opsgenie" })
{
    builder.Services.AddHttpClient(client).ConfigurePrimaryHttpMessageHandler(() => new Receiver(client, sent));
}

builder.Services.AddHttpClient("self").AddHomeostatSignals();

var app = builder.Build();
app.MapHealthChecks("/health");
app.MapHomeostatStatus(allowAnonymous: true);
app.MapHomeostatControl(allowAnonymous: true);
app.MapHomeostatDashboard(allowAnonymous: true);
app.MapHomeostatAlerts(allowAnonymous: true);

const string SlackSecret = "smoke-slack-signing-secret";
var teamsKey = RandomNumberGenerator.GetBytes(32);
app.MapHomeostatSlackInteractions(o =>
{
    o.SigningSecret = SlackSecret;
    o.AllowedUserIds.Add("U0SMOKE");
});
app.MapHomeostatTeamsCommands(o =>
{
    o.SecurityToken = Convert.ToBase64String(teamsKey);
    o.AllowAnyTeamMember = true;
});

await app.StartAsync();
var failures = new List<string>();
var step = "starting";
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var http = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("self");
    http.BaseAddress = new Uri(app.Urls.First());

    // A signal through the generic alert endpoint, until the plan runs.
    step = "healing the condition";
    while (!healed.Task.IsCompleted)
    {
        using var content = new StringContent("""{"source":"feed","severity":"Unhealthy","message":"smoke"}""", Encoding.UTF8, "application/json");
        var accepted = await http.PostAsync("/homeostat/alerts/signals", content, timeout.Token);
        Check(accepted.StatusCode == HttpStatusCode.Accepted, $"POST /homeostat/alerts/signals answered {(int)accepted.StatusCode}");
        Check(JsonNode.Parse(await accepted.Content.ReadAsStringAsync(timeout.Token))?["accepted"]?.GetValue<int>() == 1, "the signal was not accepted");
        await Task.WhenAny(healed.Task, Task.Delay(500, timeout.Token));
    }

    // The feed recovers: the step is verified and the run completes, then shows in the status endpoint.
    step = "reading the run from the status endpoint";
    JsonNode? status = null;
    while (status?["recentRuns"]?.AsArray().Count is not > 0)
    {
        using var content = new StringContent("""{"source":"feed","severity":"Healthy"}""", Encoding.UTF8, "application/json");
        (await http.PostAsync("/homeostat/alerts/signals", content, timeout.Token)).EnsureSuccessStatusCode();
        await Task.Delay(300, timeout.Token);
        status = JsonNode.Parse(await http.GetStringAsync("/homeostat/status", timeout.Token));
    }

    Check(status!["application"]?.GetValue<string>() == "aot-smoke", "status: wrong application");
    Check(status["recentRuns"]![0]!["condition"]?.GetValue<string>() == "feed-down", "status: wrong run");

    step = "recording calls";
    var calls = app.Services.GetRequiredKeyedService<Homeostat.Calls.CallRecorder>("geocoding:api");
    Check(await calls.TrackAsync(_ => Task.FromResult(42), cancellationToken: timeout.Token) == 42, "call signals: TrackAsync did not return the result");

    step = "reading coverage, history, dashboard and health";
    var coverage = JsonNode.Parse(await http.GetStringAsync("/homeostat/coverage", timeout.Token));
    Check(coverage?["conditions"]?.AsArray().Count == 3, "coverage: expected three conditions");

    var history = JsonNode.Parse(await http.GetStringAsync("/homeostat/history?condition=feed-down", timeout.Token));
    Check(history?.AsArray().Count > 0, "history: no events");

    var bad = await http.GetAsync("/homeostat/coverage?last=never", timeout.Token);
    Check(bad.StatusCode == HttpStatusCode.BadRequest, $"coverage with a bad duration answered {(int)bad.StatusCode}");

    var dashboard = await http.GetAsync("/homeostat/dashboard", timeout.Token);
    Check(dashboard.IsSuccessStatusCode, $"dashboard answered {(int)dashboard.StatusCode}");

    var health = await http.GetAsync("/health", timeout.Token);
    Check(health.IsSuccessStatusCode, $"health answered {(int)health.StatusCode}");

    // Chat endpoints: a signed Slack button click (for an unknown approval) and a signed Teams command.
    step = "calling the Slack and Teams endpoints";
    var click = new JsonObject
    {
        ["type"] = "block_actions",
        ["user"] = new JsonObject { ["id"] = "U0SMOKE", ["username"] = "smoke" },
        ["actions"] = new JsonArray(new JsonObject { ["action_id"] = "homeostat_approve", ["value"] = "unknown" }),
    }.ToJsonString();
    var form = "payload=" + Uri.EscapeDataString(click);
    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
    using var slack = new HttpRequestMessage(HttpMethod.Post, "/homeostat/slack/interactions")
    {
        Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded"),
    };
    slack.Headers.Add("X-Slack-Request-Timestamp", timestamp);
    slack.Headers.Add("X-Slack-Signature", "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SlackSecret), Encoding.UTF8.GetBytes($"v0:{timestamp}:{form}"))));
    var slackAnswer = await http.SendAsync(slack, timeout.Token);
    Check(slackAnswer.IsSuccessStatusCode, $"Slack interaction answered {(int)slackAnswer.StatusCode}");

    var command = Encoding.UTF8.GetBytes(new JsonObject
    {
        ["id"] = "smoke-1",
        ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        ["text"] = "<at>Homeostat</at> pending",
        ["from"] = new JsonObject { ["name"] = "Smoke", ["aadObjectId"] = "00000000-0000-0000-0000-000000000001" },
    }.ToJsonString());
    using var teams = new HttpRequestMessage(HttpMethod.Post, "/homeostat/teams/messages") { Content = new ByteArrayContent(command) };
    teams.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
    teams.Headers.TryAddWithoutValidation("Authorization", "HMAC " + Convert.ToBase64String(HMACSHA256.HashData(teamsKey, command)));
    var teamsAnswer = await http.SendAsync(teams, timeout.Token);
    Check(teamsAnswer.IsSuccessStatusCode && JsonNode.Parse(await teamsAnswer.Content.ReadAsStringAsync(timeout.Token))?["type"]?.GetValue<string>() == "message",
        $"Teams command answered {(int)teamsAnswer.StatusCode}");

    // Control requests without the anti-forgery header are refused with a problem details answer.
    var forged = await http.PostAsync("/homeostat/pause", null, timeout.Token);
    Check(forged.StatusCode == HttpStatusCode.Forbidden, $"pause without X-Requested-With answered {(int)forged.StatusCode}");
    Check(JsonNode.Parse(await forged.Content.ReadAsStringAsync(timeout.Token))?["status"]?.GetValue<int>() == 403, "pause without X-Requested-With: no problem details");

    using var pause = new HttpRequestMessage(HttpMethod.Post, "/homeostat/pause") { Headers = { { "X-Requested-With", "XMLHttpRequest" } } };
    var paused = await http.SendAsync(pause, timeout.Token);
    Check(paused.StatusCode == HttpStatusCode.NoContent, $"pause answered {(int)paused.StatusCode}");

    // Every notifier delivered at least one event.
    step = "waiting for the notifications";
    while (!timeout.IsCancellationRequested && sent.Select(s => s.Client).Distinct().Count() < 5)
    {
        await Task.Delay(200, timeout.Token);
    }

    Check(sent.Any(s => s.Client == WebhookOptions.HttpClientName && JsonNode.Parse(s.Body)?["text"]?.GetValue<string>() is { Length: > 0 }), "webhook: no event");
    Check(sent.Any(s => s.Client == "Homeostat.PagerDuty" && JsonNode.Parse(s.Body)?["event_action"]?.GetValue<string>() is { Length: > 0 }), "pagerduty: no event");
    Check(sent.Any(s => s.Client == "Homeostat.Opsgenie" && JsonNode.Parse(s.Body)?["source"]?.GetValue<string>() == "Homeostat"), "opsgenie: no alert");
    Check(sent.Any(s => s.Client == "Homeostat.Slack" && JsonNode.Parse(s.Body)?["blocks"] is JsonArray), "slack: no message");
    Check(sent.Any(s => s.Client == "Homeostat.Teams" && JsonNode.Parse(s.Body)?["attachments"] is JsonArray), "teams: no card");
}
catch (Exception ex)
{
    failures.Add($"while {step}: {ex}");
}
finally
{
    await app.StopAsync();
}

// What the stopped application left on disk: the store, the audit log and the recorded signals.
Check(File.Exists(Path.Combine(directory.FullName, "homeostat.json")), "the file store was not written");
Check(directory.GetFiles("audit-*.jsonl").Any(f => File.ReadAllText(f.FullName).Contains("feed-down", StringComparison.Ordinal)), "the audit log was not written");
Check(directory.GetFiles("signals-*.jsonl").Any(f => File.ReadAllText(f.FullName).Contains("alert:feed", StringComparison.Ordinal)), "the signals were not recorded");

directory.Delete(recursive: true);
foreach (var failure in failures)
{
    Console.Error.WriteLine($"FAIL {failure}");
}

Console.WriteLine(failures.Count == 0 ? "Native AOT smoke test passed." : $"Native AOT smoke test failed: {failures.Count} problem(s).");
return failures.Count == 0 ? 0 : 1;

void Check(bool condition, string problem)
{
    if (!condition)
    {
        failures.Add(problem);
    }
}

/// <summary>Accepts every request and keeps its body.</summary>
internal sealed class Receiver(string client, ConcurrentQueue<(string Client, string Body)> sent) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        sent.Enqueue((client, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
