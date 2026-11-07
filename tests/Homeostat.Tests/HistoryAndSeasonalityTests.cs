using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Homeostat.Cli;
using Homeostat.Conditions;
using Homeostat.Engine;
using Homeostat.Http;
using Homeostat.Persistence;
using Homeostat.Recording;
using Homeostat.Redis;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Tests;

public class HistoryAndSeasonalityTests
{
    // A Monday at midnight UTC.
    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- seasonality

    /// <summary>Busy from 08:00 to 20:00 (about 100), quiet at night (about 10), with a little deterministic noise.</summary>
    private static double Load(DateTimeOffset at)
    {
        var busy = at.Hour is >= 8 and < 20;
        var noise = Math.Sin(at.ToUnixTimeSeconds() / 900.0) * (busy ? 5 : 1);
        return (busy ? 100 : 10) + noise;
    }

    private static Signal Sample(DateTimeOffset at, double value) => new("requests", SignalSeverity.Healthy, at) { Value = value };

    private static ConditionTracker Seasonal(Seasonality seasonality, int minimumPeriods = 3)
    {
        var builder = new ConditionBuilder("traffic", new ServiceCollection());
        builder.ForSource("requests").WhenSeasonallyHigh(4, seasonality, minimumPeriods);
        return new ConditionTracker(builder.Build());
    }

    private static void Learn(ConditionTracker tracker, DateTimeOffset from, int days)
    {
        for (var at = from; at < from.AddDays(days); at = at.AddMinutes(15))
        {
            tracker.Observe(Sample(at, Load(at)));
        }
    }

    [Fact]
    public void Seasonal_rule_compares_with_the_same_time_of_day()
    {
        var tracker = Seasonal(Seasonality.Daily());
        Learn(tracker, Monday, 4);
        var day5 = Monday.AddDays(4);

        // The morning ramp is normal for 08:00, although far above the night before it.
        tracker.Observe(Sample(day5.AddHours(7.75), Load(day5.AddHours(7.75))));
        Assert.NotEqual(ConditionTransition.Activated, tracker.Observe(Sample(day5.AddHours(8), 100)));
        Assert.Equal(ConditionStatus.Inactive, tracker.Snapshot().Status);

        // 60 at 03:00 is normal by day but not at night.
        var night = day5.AddDays(1).AddHours(3);
        Assert.Equal(ConditionTransition.Activated, tracker.Observe(Sample(night, 60)));

        // A signal without a value does not clear it; a normal night value does.
        Assert.NotEqual(ConditionTransition.Cleared, tracker.Observe(new Signal("requests", SignalSeverity.Unhealthy, night.AddMinutes(1))));
        Assert.Equal(ConditionTransition.Cleared, tracker.Observe(Sample(night.AddMinutes(2), 10)));
    }

    [Fact]
    public void Seasonal_rule_waits_for_enough_periods()
    {
        var tracker = Seasonal(Seasonality.Daily(), minimumPeriods: 3);
        Learn(tracker, Monday, 2);   // two days: not enough

        Assert.Equal(ConditionTransition.None, tracker.Observe(Sample(Monday.AddDays(2).AddHours(3), 500)));
    }

    [Fact]
    public void Weekly_cycles_follow_the_day_of_the_week_and_the_time_zone()
    {
        var weekly = Seasonality.Weekly();
        Assert.Equal(168, weekly.Buckets);
        Assert.Equal(0, weekly.Locate(Monday.AddMinutes(30)).Bucket);
        Assert.Equal(167, weekly.Locate(Monday.AddMinutes(-30)).Bucket);        // Sunday 23:30
        Assert.Equal(weekly.Locate(Monday).Period, weekly.Locate(Monday.AddDays(6)).Period);
        Assert.Equal(weekly.Locate(Monday).Period + 1, weekly.Locate(Monday.AddDays(7)).Period);

        var rome = Seasonality.Daily(timeZone: TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome"));
        Assert.Equal(40, rome.Locate(Monday.AddHours(9)).Bucket);                // 10:00 in Rome in November

        Assert.Throws<ArgumentOutOfRangeException>(() => Seasonality.Daily(TimeSpan.FromMinutes(7)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Seasonality.Daily(TimeSpan.FromSeconds(30)));
        Assert.Throws<InvalidOperationException>(() => new ConditionBuilder("x", new ServiceCollection()).ForSource("s")
            .WhenSeasonallyHigh(3, Seasonality.Daily()).WhenSeasonallyLow(3, Seasonality.Daily()));
    }

    [Fact]
    public void A_baseline_round_trips_and_ignores_another_cycle()
    {
        var learned = new SeasonalBaseline("traffic:daily", Seasonality.Daily());
        for (var at = Monday; at < Monday.AddDays(4); at = at.AddMinutes(15))
        {
            learned.Add(Sample(at, Load(at)));
        }

        var copy = new SeasonalBaseline("traffic:daily", Seasonality.Daily());
        Assert.True(copy.Import(learned.Export()));
        var probe = Sample(Monday.AddDays(4).AddHours(3), 60);
        Assert.Equal(learned.ZScore(probe, 3), copy.ZScore(probe, 3));
        Assert.Equal(3, copy.PeriodsAt(probe.Timestamp));

        Assert.False(new SeasonalBaseline("traffic:weekly", Seasonality.Weekly()).Import(learned.Export()));
        Assert.False(copy.Import("not json"));
    }

    [Fact]
    public async Task Seasonal_baselines_survive_a_restart()
    {
        var store = new InMemoryHomeostatStore();
        void Configure(HomeostatBuilder h) => h
            .UseStore(_ => store)
            .AddCondition("traffic", c => c.ForSource("requests").WhenSeasonallyHigh(4, Seasonality.Daily()));

        await using (var first = await HomeostatTestHost.StartAsync(Configure, options: new HomeostatTestHostOptions { StartTime = Monday }))
        {
            var channel = first.Services.GetRequiredService<SignalChannel>();
            var count = 0;
            for (var at = Monday; at < Monday.AddDays(4); at = at.AddMinutes(15), count++)
            {
                first.Signals.TryPublish(Sample(at, Load(at)));
                if (count % 500 == 499)
                {
                    await PollyControlTests.WaitUntilAsync(() => channel.Processed > count - 400);
                }
            }

            await PollyControlTests.WaitUntilAsync(() => channel.Processed >= count);
        }

        Assert.NotNull(await store.GetDocumentAsync($"baseline:traffic:{Seasonality.Daily().Signature}"));

        // A new process: the anomaly is recognized at once, without days of learning.
        await using var second = await HomeostatTestHost.StartAsync(Configure, options: new HomeostatTestHostOptions { StartTime = Monday.AddDays(5) });
        second.Signals.TryPublish(Sample(Monday.AddDays(5).AddHours(3), 60));
        await second.WaitForConditionAsync("traffic", ConditionStatus.Active);
    }

    [Fact]
    public async Task Seasonal_rules_come_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Conditions:traffic:Source"] = "requests",
            ["Conditions:traffic:SeasonallyLow:ZScore"] = "3",
            ["Conditions:traffic:SeasonallyLow:Seasonality"] = "Weekly",
            ["Conditions:traffic:SeasonallyLow:Bucket"] = "30m",
            ["Conditions:traffic:SeasonallyLow:TimeZone"] = "Europe/Rome",
            ["Conditions:traffic:SeasonallyLow:MinimumPeriods"] = "2",
        }).Build();
        await using var host = await HomeostatTestHost.StartAsync(h => h.AddConditionsFromConfiguration(config.GetSection("Conditions")));

        var baseline = Assert.Single(host.Services.GetRequiredService<ConditionSet>().Trackers.Single().Definition.Baselines);
        Assert.Equal(TimeSpan.FromDays(7), baseline.Seasonality.Period);
        Assert.Equal(TimeSpan.FromMinutes(30), baseline.Seasonality.Bucket);
        Assert.Equal("Europe/Rome", baseline.Seasonality.TimeZone.Id);

        var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Conditions:traffic:Source"] = "requests",
            ["Conditions:traffic:SeasonallyHigh:ZScore"] = "3",
            ["Conditions:traffic:SeasonallyHigh:Seasonality"] = "Monthly",
        }).Build();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var _ = await HomeostatTestHost.StartAsync(h => h.AddConditionsFromConfiguration(invalid.GetSection("Conditions"), failOnInvalid: true));
        });
    }

    // ---------------------------------------------------------------- condition history

    [Fact]
    public async Task Condition_history_is_kept_in_the_store()
    {
        var store = new InMemoryHomeostatStore();
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .UseStore(_ => store)
            .Configure(o => { o.InstanceId = "web-1"; o.ConditionHistorySize = 3; })
            .AddCondition("feed-down", c => c.ForSource("feed")));

        for (var i = 0; i < 2; i++)
        {
            host.Publish("feed", SignalSeverity.Unhealthy, "timeout");
            await host.WaitForConditionAsync("feed-down", ConditionStatus.Active);
            host.Publish("feed", SignalSeverity.Healthy);
            await host.WaitForConditionAsync("feed-down", ConditionStatus.Inactive);
        }

        await PollyControlTests.WaitUntilAsync(() => store.GetConditionEventsAsync(10).AsTask().Result is [{ Kind: ConditionEventKind.Cleared }, _, _]);
        var history = await host.Control.GetConditionHistoryAsync();

        Assert.Equal([ConditionEventKind.Cleared, ConditionEventKind.Activated, ConditionEventKind.Cleared], history.Select(e => e.Kind));
        Assert.Equal("timeout", history[1].Message);
        Assert.All(history, e => Assert.Equal(("feed-down", "web-1"), (e.ConditionName, e.Instance)));
    }

    /// <summary>A store written before the condition history existed: only the original members.</summary>
    private sealed class OldStore : IHomeostatStore
    {
        private readonly InMemoryHomeostatStore _inner = new();

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => _inner.GetPlanStateAsync(key, cancellationToken);

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) => _inner.UpdatePlanStateAsync(key, update, cancellationToken);

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken);

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => _inner.GetBudgetUsedAsync(now, window, cancellationToken);

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => _inner.AppendRunAsync(run, maxRuns, cancellationToken);

        public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) => _inner.GetRecentRunsAsync(maxRuns, cancellationToken);

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) => _inner.GetControlStateAsync(cancellationToken);

        public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetPauseAsync(pause, decidedAt, cancellationToken);

        public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetSwitchAsync(name, state, decidedAt, cancellationToken);

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) => _inner.TryClaimSwitchAsync(state, cancellationToken);

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
            => _inner.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken);

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) => _inner.AddApprovalAsync(approval, cancellationToken);

        public ValueTask<bool> TryResolveApprovalAsync(string id, Homeostat.Healing.ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default)
            => _inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken);

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) => _inner.RemoveApprovalAsync(id, cancellationToken);
    }

    [Fact]
    public async Task A_store_without_condition_history_falls_back_to_memory()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .UseStore(_ => new OldStore())
            .AddCondition("feed-down", c => c.ForSource("feed")));

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForConditionAsync("feed-down", ConditionStatus.Active);

        var history = await host.Control.GetConditionHistoryAsync();
        Assert.Equal(ConditionEventKind.Activated, Assert.Single(history).Kind);
        Assert.Equal(1, (await host.Control.GetCoverageAsync(host.Time.GetUtcNow().AddMinutes(-1))).Conditions.Single().Activations);
    }

    [Fact]
    public async Task Coverage_counts_the_activations_of_every_instance_from_the_history()
    {
        var store = new InMemoryHomeostatStore();
        var at = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await store.AppendConditionEventAsync(new ConditionEventRecord { ConditionName = "feed-down", Kind = ConditionEventKind.Activated, At = at.AddMinutes(-5), Instance = "web-2" }, 100);
        await store.AppendConditionEventAsync(new ConditionEventRecord { ConditionName = "feed-down", Kind = ConditionEventKind.Activated, At = at.AddHours(-3), Instance = "web-2" }, 100);

        await using var host = await HomeostatTestHost.StartAsync(h => h
            .UseStore(_ => store)
            .Configure(o => o.InstanceId = "web-1")
            .AddCondition("feed-down", c => c.ForSource("feed")));
        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForConditionAsync("feed-down", ConditionStatus.Active);

        // web-2's recent activation and this one, whether or not it has been written to the store yet.
        var report = await host.Control.GetCoverageAsync(at.AddHours(-1));
        Assert.Equal(2, report.Conditions.Single().Activations);
    }

    [Fact]
    public async Task File_store_keeps_condition_history_and_documents()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("homeostat-store-").FullName, "state.json");
        using (var store = new FileHomeostatStore(path))
        {
            await store.AppendConditionEventAsync(new ConditionEventRecord { ConditionName = "feed-down", Kind = ConditionEventKind.Activated, At = Monday, Instance = "web-1", Value = 3.5 }, 10);
            await store.SetDocumentAsync("baseline:x", "{\"a\":1}");
        }

        using var reopened = new FileHomeostatStore(path);
        var e = Assert.Single(await reopened.GetConditionEventsAsync(10));
        Assert.Equal(("feed-down", ConditionEventKind.Activated, 3.5), (e.ConditionName, e.Kind, e.Value!.Value));
        Assert.Equal("{\"a\":1}", await reopened.GetDocumentAsync("baseline:x"));
        Assert.Null(await reopened.GetDocumentAsync("baseline:y"));
    }

    [Fact]
    public async Task Redis_store_keeps_condition_history_and_documents()
    {
        using var redis = RedisFixture.Connect();
        var store = new RedisHomeostatStore(redis, Options.Create(new RedisStoreOptions { KeyPrefix = RedisFixture.Prefix() }), Options.Create(new HomeostatOptions()));

        Assert.True(((IHomeostatStore)store).SupportsConditionHistory);
        for (var i = 0; i < 4; i++)
        {
            await store.AppendConditionEventAsync(new ConditionEventRecord { ConditionName = $"c{i}", Kind = ConditionEventKind.Cleared, At = Monday.AddMinutes(i) }, 3);
        }

        Assert.Equal(["c3", "c2", "c1"], (await store.GetConditionEventsAsync(10)).Select(e => e.ConditionName));
        Assert.Null(await store.GetDocumentAsync("baseline:x"));
        await store.SetDocumentAsync("baseline:x", "{}");
        Assert.Equal("{}", await store.GetDocumentAsync("baseline:x"));
    }

    [Fact]
    public async Task History_is_served_and_printed_by_the_cli()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat(h => h
            .AddCondition("feed-down", c => c.ForSource("feed"))
            .AddCondition("disk-full", c => c.ForSource("disk")));
        var app = builder.Build();
        app.MapHomeostatStatus(allowAnonymous: true);
        await app.StartAsync();
        await using var _ = app;

        var publisher = app.Services.GetRequiredService<ISignalPublisher>();
        publisher.TryPublish("feed", SignalSeverity.Unhealthy, "timeout");
        publisher.TryPublish("disk", SignalSeverity.Unhealthy);
        var control = app.Services.GetRequiredService<IHomeostatControl>();
        await PollyControlTests.WaitUntilAsync(() => control.GetConditionHistoryAsync().AsTask().Result.Count == 2);

        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.First()) };
        var json = JsonDocument.Parse(await client.GetStringAsync("/homeostat/history?condition=feed-down")).RootElement;
        var e = Assert.Single(json.EnumerateArray());
        Assert.Equal(("feed-down", "Activated", "Unhealthy"), (e.GetProperty("condition").GetString(), e.GetProperty("kind").GetString(), e.GetProperty("severity").GetString()));

        var output = new StringWriter();
        Assert.Equal(0, await HomeostatCli.InvokeAsync(["history", "--url", app.Urls.First()], output, new StringWriter(), new HttpClientHandler { UseProxy = false }));
        Assert.Contains("feed-down", output.ToString());
        Assert.Contains("timeout", output.ToString());

        var dashboard = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Homeostat.AspNetCore", "Dashboard", "dashboard.js"));
        Assert.Contains("getJson(\"history\")", dashboard);
        Assert.Contains("getJson(\"coverage\")", dashboard);
    }

    // ---------------------------------------------------------------- replay from configuration

    private static async Task<string> WriteRecordingAsync(string directory)
    {
        var start = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var lines = Enumerable.Range(0, 13)
            .Select(i => new RecordedSignal { Timestamp = start.AddSeconds(i * 10), Source = "weather-feed", Severity = SignalSeverity.Unhealthy, Message = "timeout" })
            .Append(new RecordedSignal { Timestamp = start.AddSeconds(130), Source = "weather-feed", Severity = SignalSeverity.Healthy })
            .Select(s => JsonSerializer.Serialize(s, RecordedSignal.Json));
        var file = Path.Combine(directory, "signals-web-1-2026-11-01.jsonl");
        await File.WriteAllLinesAsync(file, lines);
        return file;
    }

    private const string Settings = """
        {
          "Homeostat": {
            "Conditions": {
              "feed-down": {
                "Source": "weather-feed",
                "SustainedFor": "30s",
                "Plan": { "Steps": [ { "Action": "renew-feed-client" }, { "Action": "switch:serve-cached", "TurnOffWhenCleared": true } ] }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task Replay_evaluates_a_configuration_without_the_application_actions()
    {
        var directory = Directory.CreateTempSubdirectory("homeostat-replay-").FullName;
        try
        {
            var file = await WriteRecordingAsync(directory);
            var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(Settings))).Build();

            var report = await HomeostatReplay.RunFilesAsync([file], config.GetSection("Homeostat:Conditions"));

            var summary = Assert.Single(report.Conditions);
            Assert.Equal((1, 1), (summary.Activations, summary.WouldHeal));
            Assert.Equal(["renew-feed-client", "Turn on switch 'serve-cached'"], summary.PlanSteps);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cli_replays_a_recording_against_a_configuration_file()
    {
        var directory = Directory.CreateTempSubdirectory("homeostat-replay-").FullName;
        try
        {
            await WriteRecordingAsync(directory);
            var settings = Path.Combine(directory, "appsettings.json");
            await File.WriteAllTextAsync(settings, Settings);

            var output = new StringWriter();
            var code = await HomeostatCli.InvokeAsync(["replay", directory, "--config", settings], output, new StringWriter());
            Assert.Equal(0, code);
            Assert.Contains("feed-down: active 1 times, would heal 1 times", output.ToString());

            Assert.Equal(HomeostatCli.TooManyHeals, await HomeostatCli.InvokeAsync(["replay", directory, "--config", settings, "--max-heals", "0"], new StringWriter(), new StringWriter()));

            var json = new StringWriter();
            Assert.Equal(0, await HomeostatCli.InvokeAsync(["replay", directory, "--config", settings, "--json"], json, new StringWriter()));
            Assert.Equal(14, JsonDocument.Parse(json.ToString()).RootElement.GetProperty("signals").GetInt32());

            var error = new StringWriter();
            Assert.Equal(1, await HomeostatCli.InvokeAsync(["replay", directory, "--config", Path.Combine(directory, "missing.json")], new StringWriter(), error));
            Assert.Contains("not found", error.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---------------------------------------------------------------- Teams

    private static readonly string Token = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private const string OpsUser = "6f1c2a77-5b8e-4d55-9a8e-0c2f1e0b9d41";

    private static async Task<(WebApplication App, HttpClient Client)> StartTeamsAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat(h => h
            .Configure(o => o.DryRun = false)
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                .With("failover", _ => { }).RequireApproval(TimeSpan.FromMinutes(5))
                .UseVerifier<BuiltInActionTests.AlwaysHealed>())));
        var app = builder.Build();
        app.MapHomeostatTeamsCommands(o =>
        {
            o.SecurityToken = Token;
            o.AllowedUserIds.Add(OpsUser);
        });
        await app.StartAsync();
        return (app, new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static HttpRequestMessage TeamsRequest(string text, string userId = OpsUser, DateTimeOffset? at = null, string? id = null, string? token = null)
    {
        var body = JsonSerializer.Serialize(new
        {
            type = "message",
            id = id ?? Guid.NewGuid().ToString("N"),
            timestamp = (at ?? DateTimeOffset.UtcNow).ToString("O"),
            text = $"<at>Homeostat</at>&nbsp;{text}\n",
            from = new { id = "29:1", name = "Ana", aadObjectId = userId },
        });
        var bytes = Encoding.UTF8.GetBytes(body);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(token ?? Token), bytes));
        var request = new HttpRequestMessage(HttpMethod.Post, "/homeostat/teams/messages") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation("Authorization", $"HMAC {signature}");
        return request;
    }

    private static async Task<string> ReplyAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("message", json.GetProperty("type").GetString());
        return json.GetProperty("text").GetString()!;
    }

    private static async Task<string> WaitForApprovalAsync(WebApplication app)
    {
        var approvals = app.Services.GetRequiredService<IHomeostatApprovals>();
        app.Services.GetRequiredService<ISignalPublisher>().TryPublish("feed", SignalSeverity.Unhealthy);
        await PollyControlTests.WaitUntilAsync(() => approvals.GetPending().Count == 1);
        return approvals.GetPending()[0].Id;
    }

    [Fact]
    public async Task Teams_members_list_and_approve_steps_with_signed_messages()
    {
        var (app, client) = await StartTeamsAsync();
        await using var _ = app;
        using var __ = client;
        var id = await WaitForApprovalAsync(app);

        Assert.Contains(id, await ReplyAsync(client, TeamsRequest("pending", userId: "someone-else")));
        Assert.Contains("not allowed", await ReplyAsync(client, TeamsRequest($"approve {id}", userId: "someone-else")));
        Assert.Single(app.Services.GetRequiredService<IHomeostatApprovals>().GetPending());

        Assert.Contains("Approved by Ana", await ReplyAsync(client, TeamsRequest($"approve {id}")));
        var control = app.Services.GetRequiredService<IHomeostatControl>();
        await PollyControlTests.WaitUntilAsync(() => control.GetRecentRunsAsync().AsTask().Result.Count == 1);
        Assert.Equal(Healing.HealingRunOutcome.Healed, (await control.GetRecentRunsAsync())[0].Outcome);
        Assert.Contains("not pending", await ReplyAsync(client, TeamsRequest($"reject {id}")));
        Assert.Contains("pending", await ReplyAsync(client, TeamsRequest("hello")));
    }

    [Fact]
    public async Task Teams_requests_must_be_signed_recent_and_new()
    {
        var (app, client) = await StartTeamsAsync();
        await using var _ = app;
        using var __ = client;
        var id = await WaitForApprovalAsync(app);

        var otherToken = Convert.ToBase64String(new byte[32]);
        using (var forged = await client.SendAsync(TeamsRequest($"approve {id}", token: otherToken)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        }

        using (var unsigned = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/homeostat/teams/messages") { Content = new StringContent("{}") }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);
        }

        Assert.Contains("too old", await ReplyAsync(client, TeamsRequest($"approve {id}", at: DateTimeOffset.UtcNow.AddMinutes(-10))));
        Assert.Contains(id, await ReplyAsync(client, TeamsRequest("pending", id: "m-1")));
        Assert.Contains("already handled", await ReplyAsync(client, TeamsRequest($"approve {id}", id: "m-1")));
        Assert.Single(app.Services.GetRequiredService<IHomeostatApprovals>().GetPending());

        Assert.Throws<ArgumentException>(() => app.MapHomeostatTeamsCommands(o => o.SecurityToken = Token, "/other"));   // nobody allowed
        Assert.Throws<ArgumentException>(() => app.MapHomeostatTeamsCommands(o => { o.SecurityToken = "not base64!"; o.AllowAnyTeamMember = true; }, "/other"));
    }

    [Fact]
    public void Teams_approval_cards_say_how_to_answer_from_the_channel()
    {
        var e = new Notifications.HomeostatEvent { Kind = Notifications.HomeostatEventKind.ApprovalRequested, Timestamp = DateTimeOffset.UnixEpoch, Application = "weather-api", Condition = "feed-down", Summary = "Approval needed.", ApprovalId = "a1b2" };

        var with = JsonDocument.Parse(TeamsSink.Payload(e, new TeamsOptions { ApprovalCommand = "Homeostat" }).ToJsonString(HttpEventSink.Json)).RootElement;
        var without = JsonDocument.Parse(TeamsSink.Payload(e, new TeamsOptions()).ToJsonString(HttpEventSink.Json)).RootElement;

        var body = with.GetProperty("attachments")[0].GetProperty("content").GetProperty("body");
        Assert.Contains("@Homeostat approve a1b2", body[3].GetProperty("text").GetString());
        Assert.Equal(3, without.GetProperty("attachments")[0].GetProperty("content").GetProperty("body").GetArrayLength());
    }

    // ---------------------------------------------------------------- review fixes

    [Fact]
    public async Task Teams_messages_without_id_or_timestamp_are_refused()
    {
        var (app, client) = await StartTeamsAsync();
        await using var _ = app;
        using var __ = client;

        var body = Encoding.UTF8.GetBytes("""{ "type": "message", "text": "<at>Homeostat</at> pending", "from": { "name": "Ana", "aadObjectId": "x" } }""");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/homeostat/teams/messages") { Content = new ByteArrayContent(body) };
        request.Headers.TryAddWithoutValidation("Authorization", "HMAC " + Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(Token), body)));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A store whose condition history can fail, as during a network outage.</summary>
    private sealed class FlakyStore : IHomeostatStore
    {
        private readonly InMemoryHomeostatStore _inner = new();

        public volatile bool Failing;

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => _inner.GetPlanStateAsync(key, cancellationToken);

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) => _inner.UpdatePlanStateAsync(key, update, cancellationToken);

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken);

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => _inner.GetBudgetUsedAsync(now, window, cancellationToken);

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => _inner.AppendRunAsync(run, maxRuns, cancellationToken);

        public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) => _inner.GetRecentRunsAsync(maxRuns, cancellationToken);

        public bool SupportsConditionHistory => true;

        public ValueTask AppendConditionEventAsync(ConditionEventRecord conditionEvent, int maxEvents, CancellationToken cancellationToken = default)
            => Failing ? throw new IOException("store down") : _inner.AppendConditionEventAsync(conditionEvent, maxEvents, cancellationToken);

        public ValueTask<IReadOnlyList<ConditionEventRecord>> GetConditionEventsAsync(int maxEvents, CancellationToken cancellationToken = default)
            => Failing ? throw new IOException("store down") : _inner.GetConditionEventsAsync(maxEvents, cancellationToken);

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) => _inner.GetControlStateAsync(cancellationToken);

        public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetPauseAsync(pause, decidedAt, cancellationToken);

        public ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TrySetSwitchAsync(name, state, decidedAt, cancellationToken);

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) => _inner.TryClaimSwitchAsync(state, cancellationToken);

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
            => _inner.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken);

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) => _inner.AddApprovalAsync(approval, cancellationToken);

        public ValueTask<bool> TryResolveApprovalAsync(string id, Homeostat.Healing.ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default)
            => _inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken);

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) => _inner.RemoveApprovalAsync(id, cancellationToken);
    }

    [Fact]
    public async Task Condition_events_survive_a_store_outage()
    {
        var store = new FlakyStore { Failing = true };
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .UseStore(_ => store)
            .AddCondition("feed-down", c => c.ForSource("feed")));

        host.Publish("feed", SignalSeverity.Unhealthy);
        await host.WaitForConditionAsync("feed-down", ConditionStatus.Active);

        // While the store is down, the history still shows the event.
        Assert.Equal(ConditionEventKind.Activated, Assert.Single(await host.Control.GetConditionHistoryAsync()).Kind);

        // When it recovers, the event is written by the next retry.
        store.Failing = false;
        await PollyControlTests.WaitUntilAsync(() =>
        {
            host.Time.Advance(TimeSpan.FromSeconds(31));
            return store.GetConditionEventsAsync(10).AsTask().Result.Count == 1;
        });
        Assert.Single(await host.Control.GetConditionHistoryAsync());
    }

    [Fact]
    public void A_changed_condition_keeps_its_seasonal_baseline()
    {
        ConditionDefinition Build(TimeSpan sustain)
        {
            var builder = new ConditionBuilder("traffic", new ServiceCollection());
            builder.ForSource("requests").WhenSeasonallyHigh(4, Seasonality.Daily()).SustainedFor(sustain);
            return builder.Build();
        }

        var first = Build(TimeSpan.Zero);
        var set = new ConditionSet([first]);
        for (var at = Monday; at < Monday.AddDays(4); at = at.AddMinutes(15))
        {
            first.Record!(Sample(at, Load(at)));
        }

        var second = Build(TimeSpan.FromMinutes(1));
        set.Replace([second]);

        Assert.Equal(3, second.Baselines[0].PeriodsAt(Monday.AddDays(4)));

        Assert.Throws<InvalidOperationException>(() =>
        {
            var builder = new ConditionBuilder("x", new ServiceCollection());
            builder.ForSource("s").WhenSeasonallyHigh(3, Seasonality.Daily()).WhenValueAbove(5);
            builder.Build();
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => Seasonality.Weekly(TimeSpan.FromMinutes(1)));   // too many slots
    }

    [Fact]
    public async Task Replay_merges_the_files_of_several_instances_in_time_order()
    {
        var directory = Directory.CreateTempSubdirectory("homeostat-replay-").FullName;
        try
        {
            var start = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
            async Task WriteAsync(string name, params int[] seconds) => await File.WriteAllLinesAsync(Path.Combine(directory, name),
                seconds.Select(s => JsonSerializer.Serialize(new RecordedSignal { Timestamp = start.AddSeconds(s), Source = "feed", Severity = SignalSeverity.Unhealthy }, RecordedSignal.Json)));
            await WriteAsync("signals-a-2026-11-01.jsonl", 30, 40);
            await WriteAsync("signals-b-2026-11-01.jsonl", 0, 10, 20);

            var report = await HomeostatReplay.RunFilesAsync(Directory.GetFiles(directory), h => h
                .AddCondition("feed-down", c => c.ForSource("feed").SustainedFor(TimeSpan.FromSeconds(30))));

            Assert.Equal(start, report.Since);
            Assert.Equal(start.AddSeconds(30), Assert.Single(report.Events).At);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Replay_from_configuration_uses_the_application_options()
    {
        var start = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        var signals = new[] { 0, 600 }.SelectMany(offset => new[]
        {
            new RecordedSignal { Timestamp = start.AddSeconds(offset), Source = "feed", Severity = SignalSeverity.Unhealthy },
            new RecordedSignal { Timestamp = start.AddSeconds(offset + 60), Source = "feed", Severity = SignalSeverity.Healthy },
        }).ToList();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Conditions:feed-down:Source"] = "feed",
            ["Conditions:feed-down:Plan:Steps:0:Action"] = "renew",
        }).Build().GetSection("Conditions");

        var defaults = await HomeostatReplay.RunAsync(signals, config);
        var production = await HomeostatReplay.RunAsync(signals, config, o => o.DefaultCooldown = TimeSpan.FromHours(1));

        Assert.Equal(2, defaults.Conditions.Single().WouldHeal);
        Assert.Equal(1, production.Conditions.Single().WouldHeal);
    }

    [Fact]
    public async Task Conditions_from_configuration_without_mapped_actions_start_in_a_real_host()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Homeostat:Conditions:feed-down:Source"] = "feed",
            ["Homeostat:Conditions:feed-down:Plan:Steps:0:Action"] = "switch:serve-cached",
        });
        builder.Services.AddHomeostat(h => h.AddConditionsFromConfiguration(builder.Configuration.GetSection("Homeostat:Conditions")));
        var app = builder.Build();
        await app.StartAsync();
        await using var _ = app;

        Assert.Equal("feed-down", Assert.Single(app.Services.GetRequiredService<IHomeostatControl>().GetConditions()).Name);
    }
}
