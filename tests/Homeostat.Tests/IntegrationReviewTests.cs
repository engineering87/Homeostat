using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Homeostat.FeatureManagement;
using Homeostat.Healing;
using Homeostat.Persistence;
using Homeostat.Polly;
using Homeostat.ServiceBus;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Registry;

namespace Homeostat.Tests;

/// <summary>Fixes from the review of the integration packages (ASP.NET Core, health checks, brokers, cloud, Polly).</summary>
public class IntegrationReviewTests
{
    // ---------------------------------------------------------------- Service Bus: a pause before the processor starts

    private sealed class StartLaterProcessor : ServiceBusProcessor
    {
        private volatile bool _processing;

        public int Starts;

        public int Stops;

        public override bool IsProcessing => _processing;

        public volatile bool Closed;

        public override bool IsClosed => Closed;

        public override Task StartProcessingAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Starts);
            _processing = true;
            return Task.CompletedTask;
        }

        public override Task StopProcessingAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Stops);
            _processing = false;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_Service_Bus_processor_paused_before_it_starts_is_stopped_when_it_starts_and_resumed_after()
    {
        var processor = new StartLaterProcessor();
        var pausable = new PausableServiceBusProcessor("ingest", processor, TimeSpan.FromMilliseconds(10));

        // The switch was already on at startup: the pause finds the processor not started yet.
        await pausable.PauseAsync(CancellationToken.None);
        Assert.Equal(0, processor.Stops);

        // The application starts it afterwards: the standing pause stops it.
        await processor.StartProcessingAsync();
        await PollyControlTests.WaitUntilAsync(() => !processor.IsProcessing);

        // Stopped by Homeostat, so the resume starts it, and the watch ends with the resume.
        await pausable.ResumeAsync(CancellationToken.None);
        Assert.True(processor.IsProcessing);
        await Task.Delay(100);
        Assert.True(processor.IsProcessing);
        Assert.Equal(1, processor.Stops);
    }

    [Fact]
    public async Task A_Service_Bus_processor_the_application_never_started_is_not_started_by_the_resume()
    {
        var processor = new StartLaterProcessor();
        var pausable = new PausableServiceBusProcessor("ingest", processor, TimeSpan.FromMilliseconds(10));

        await pausable.PauseAsync(CancellationToken.None);
        await Task.Delay(50);
        await pausable.ResumeAsync(CancellationToken.None);

        Assert.Equal((0, 0), (processor.Starts, processor.Stops));   // I5: the application decides when it starts

        // A processor closed while paused ends the watch, and is never started.
        var closed = new StartLaterProcessor();
        var other = new PausableServiceBusProcessor("ingest", closed, TimeSpan.FromMilliseconds(10));
        await other.PauseAsync(CancellationToken.None);
        closed.Closed = true;
        await Task.Delay(50);
        await other.ResumeAsync(CancellationToken.None);
        Assert.Equal(0, closed.Starts);
    }

    // ---------------------------------------------------------------- Azure Front Door: the last origin

    private const string Subscription = "/subscriptions/00000000-1111-2222-3333-444444444444/resourceGroups/weather-rg";
    private const string Origin = Subscription + "/providers/Microsoft.Cdn/profiles/weather-fd/originGroups/api/origins/westeurope";
    private const string OtherOrigin = Subscription + "/providers/Microsoft.Cdn/profiles/weather-fd/originGroups/api/origins/northeurope";

    [Fact]
    public async Task A_Front_Door_origin_is_enabled_again_when_the_other_origins_were_disabled_meanwhile()
    {
        // The group lists the other origin as enabled before the change, and as disabled after it (another plan
        // disabled it between the check and the change).
        var groupReads = 0;
        var arm = new AzureTests.FakeArm
        {
            Respond = (method, uri) =>
            {
                if (method != "GET")
                {
                    return string.Empty;
                }

                if (!uri.Contains("/origins?", StringComparison.Ordinal))
                {
                    return $$$"""{"id":"{{{Origin}}}","properties":{"enabledState":"Enabled"}}""";
                }

                var other = Interlocked.Increment(ref groupReads) == 1 ? "Enabled" : "Disabled";
                return $$$"""{"value":[{"id":"{{{Origin}}}","properties":{"enabledState":"Disabled"}},{"id":"{{{OtherOrigin}}}","properties":{"enabledState":"{{{other}}}"}}]}""";
            },
        };

        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .UseAzure(o => o.AccessToken = (_, _) => ValueTask.FromResult("arm-token"))
                .AddCondition("api-down", c => c.ForSource("api").Heal(p => p
                    .DisableFrontDoorOrigin(Origin, requireApproval: false)
                    .VerifyWith<CachingTests.NeverHealed>())),
            s => s.AddHttpClient("Homeostat.Azure").ConfigurePrimaryHttpMessageHandler(() => arm));

        host.Publish("api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("api-down");

        var step = Assert.Single(run.Steps);
        Assert.False(step.Succeeded);
        Assert.Contains("was enabled again", step.Error!.Message);
        var patches = arm.Calls.Where(c => c.Method == "PATCH").ToList();
        Assert.Equal(["Disabled", "Enabled"], patches.Select(c => c.Body!.Value.GetProperty("properties").GetProperty("enabledState").GetString()!));
        Assert.Equal(2, groupReads);
    }

    // ---------------------------------------------------------------- control endpoints

    private sealed class ScriptedStore : IHomeostatStore
    {
        private readonly InMemoryHomeostatStore _inner = new();

        /// <summary>When set, every control state read and write fails with it.</summary>
        public volatile Exception? Fail;

        /// <summary>When set, switch writes wait for it, and record the token they were given.</summary>
        public TaskCompletionSource? HoldSwitchWrites;

        public CancellationToken LastSwitchToken;

        public volatile bool WriteStarted;

        private void Check()
        {
            if (Fail is { } fail)
            {
                throw fail;
            }
        }

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => _inner.GetPlanStateAsync(key, cancellationToken);

        /// <summary>Whether <see cref="Fail"/> also applies to plan state updates (the release of a hold).</summary>
        public volatile bool FailPlanState;

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default)
        {
            if (FailPlanState)
            {
                Check();
            }

            return _inner.UpdatePlanStateAsync(key, update, cancellationToken);
        }

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => _inner.TryConsumeBudgetAsync(now, limit, window, cancellationToken);

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default)
        {
            Check();
            return _inner.GetBudgetUsedAsync(now, window, cancellationToken);
        }

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => _inner.AppendRunAsync(run, maxRuns, cancellationToken);

        public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) => _inner.GetRecentRunsAsync(maxRuns, cancellationToken);

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default)
        {
            Check();
            return _inner.GetControlStateAsync(cancellationToken);
        }

        public ValueTask<bool> TrySetPauseAsync(PauseState? pause, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
        {
            Check();
            return _inner.TrySetPauseAsync(pause, decidedAt, cancellationToken);
        }

        public async ValueTask<bool> TrySetSwitchAsync(string name, SwitchState? state, DateTimeOffset decidedAt, CancellationToken cancellationToken = default)
        {
            Check();
            if (HoldSwitchWrites is { } hold)
            {
                LastSwitchToken = cancellationToken;
                WriteStarted = true;
                await hold.Task.ConfigureAwait(false);
            }

            return await _inner.TrySetSwitchAsync(name, state, decidedAt, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) => _inner.TryClaimSwitchAsync(state, cancellationToken);

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, DateTimeOffset decidedAt, CancellationToken cancellationToken = default) => _inner.TryReleaseSwitchAsync(name, condition, instance, decidedAt, cancellationToken);

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) => _inner.AddApprovalAsync(approval, cancellationToken);

        public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default)
        {
            Check();
            return _inner.TryResolveApprovalAsync(id, status, resolvedBy, cancellationToken);
        }

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) => _inner.RemoveApprovalAsync(id, cancellationToken);
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Category, string Message, Exception? Exception)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CollectingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries)
                {
                    owner.Entries.Add((logLevel, category, formatter(state, exception), exception));
                }
            }
        }
    }

    private static async Task<(WebApplication App, HttpClient Client)> StartWebAsync(
        ScriptedStore store, Action<HomeostatBuilder>? configure = null, CollectingLoggerProvider? logs = null, Action<WebApplication>? map = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (logs is not null)
        {
            builder.Logging.AddProvider(logs);
        }

        builder.Services.AddHomeostat(h =>
        {
            h.Configure(o => o.DryRun = false).UseStore(_ => store);
            configure?.Invoke(h);
        });
        var app = builder.Build();
        app.MapHomeostatStatus(allowAnonymous: true);
        app.MapHomeostatControl(allowAnonymous: true);
        map?.Invoke(app);
        await app.StartAsync();
        var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Add("X-Requested-With", "tests");
        return (app, client);
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostAsync(HttpClient client, string path)
    {
        using var response = await client.PostAsync(path, null);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_store_timeout_answers_that_the_change_may_still_apply_and_a_failure_that_nothing_changed()
    {
        var store = new ScriptedStore();
        var (app, client) = await StartWebAsync(store, h => h.AddCondition("feed/eu", c => c.ForSource("feed").Heal(p => p.With("renew", _ => { }))));
        await using var _ = app;
        using var __ = client;

        store.Fail = new TimeoutException("redis-01.internal:6379 timed out");

        // A turn-on (or a resume) that times out is decided again in the safe direction (S4): it was not made.
        var (status, body) = await PostAsync(client, "/homeostat/switches/serve-cached/on");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("was not made", body);
        Assert.False(body.Contains("redis-01", StringComparison.Ordinal));

        // The release of a hold that times out may still be applied by the store.
        store.FailPlanState = true;
        (status, body) = await PostAsync(client, $"/homeostat/conditions/{Uri.EscapeDataString("feed/eu")}/release");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("may still be applied", body);
        Assert.False(body.Contains("redis-01", StringComparison.Ordinal));

        store.Fail = new InvalidOperationException("connection refused by redis-01.internal");
        (status, body) = await PostAsync(client, "/homeostat/switches/serve-cached/on");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Contains("nothing was changed", body);
        Assert.False(body.Contains("redis-01", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Status_reports_the_store_error_without_its_message()
    {
        var store = new ScriptedStore();
        var logs = new CollectingLoggerProvider();
        var (app, client) = await StartWebAsync(store, logs: logs);
        await using var _ = app;
        using var __ = client;

        store.Fail = new InvalidOperationException("connection refused by redis-01.internal");
        using var response = await client.GetAsync("/homeostat/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("storeError").GetString()!;

        Assert.Contains(nameof(InvalidOperationException), error);
        Assert.False(error.Contains("redis-01", StringComparison.Ordinal));
        lock (logs.Entries)
        {
            Assert.Contains(logs.Entries, e => e.Exception?.Message.Contains("redis-01", StringComparison.Ordinal) == true);
        }
    }

    [Fact]
    public async Task Names_with_a_slash_reach_the_switch_and_condition_they_name()
    {
        var store = new ScriptedStore();
        var (app, client) = await StartWebAsync(store, h => h.AddCondition("feed/eu", c => c.ForSource("feed").Heal(p => p.With("renew", _ => { }))));
        await using var _ = app;
        using var __ = client;
        var switches = app.Services.GetRequiredService<IHomeostatSwitches>();

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, $"/homeostat/switches/{Uri.EscapeDataString("shed/maps")}/on")).Status);
        Assert.True(switches.IsOn("shed/maps"));
        Assert.False(switches.IsOn("shed%2Fmaps"));
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, "/homeostat/switches/shed%2fmaps/off")).Status);
        Assert.False(switches.IsOn("shed/maps"));

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync(client, $"/homeostat/conditions/{Uri.EscapeDataString("feed/eu")}/release")).Status);

        // Condition names have no length limit (unlike switch names): a long one is looked up, not refused; control
        // characters are refused.
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, $"/homeostat/conditions/{new string('x', 201)}/release")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/homeostat/conditions/feed%01eu/release")).Status);
    }

    [Fact]
    public async Task A_client_that_disconnects_does_not_cut_a_change_short_nor_raise_an_unhandled_error()
    {
        var store = new ScriptedStore();
        var logs = new CollectingLoggerProvider();
        var (app, client) = await StartWebAsync(store, logs: logs);
        await using var _ = app;
        using var __ = client;

        store.HoldSwitchWrites = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var gone = new CancellationTokenSource())
        {
            var request = client.PostAsync("/homeostat/switches/serve-cached/on", null, gone.Token);
            await PollyControlTests.WaitUntilAsync(() => store.WriteStarted);
            await gone.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }

        await Task.Delay(200);   // the server notices the disconnect
        store.HoldSwitchWrites.SetResult();

        // The change was made with no request token, so it completes.
        Assert.False(store.LastSwitchToken.CanBeCanceled);
        var switches = app.Services.GetRequiredService<IHomeostatSwitches>();
        await PollyControlTests.WaitUntilAsync(() => switches.IsOn("serve-cached"));

        await Task.Delay(200);
        lock (logs.Entries)
        {
            Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
        }
    }

    // ---------------------------------------------------------------- Teams replay entry on a timeout

    private static readonly string TeamsToken = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    private static HttpRequestMessage TeamsRequest(string text, string id)
    {
        var body = JsonSerializer.Serialize(new
        {
            type = "message",
            id,
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            text = $"<at>Homeostat</at>&nbsp;{text}\n",
            from = new { id = "29:1", name = "Ana", aadObjectId = "ops" },
        });
        var bytes = Encoding.UTF8.GetBytes(body);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(TeamsToken), bytes));
        var request = new HttpRequestMessage(HttpMethod.Post, "/homeostat/teams/messages") { Content = new ByteArrayContent(bytes) };
        request.Headers.TryAddWithoutValidation("Authorization", $"HMAC {signature}");
        return request;
    }

    private static async Task<string> TeamsReplyAsync(HttpClient client, HttpRequestMessage request)
    {
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("text").GetString()!;
    }

    [Fact]
    public async Task Teams_keeps_a_timed_out_message_as_handled_and_lets_a_failed_one_be_sent_again()
    {
        var store = new ScriptedStore();
        var (app, client) = await StartWebAsync(store, map: a => a.MapHomeostatTeamsCommands(o =>
        {
            o.SecurityToken = TeamsToken;
            o.AllowedUserIds.Add("ops");
        }));
        await using var _ = app;
        using var __ = client;

        // A timeout: the command may still be carried out, so a replay of the same message is not run again.
        store.Fail = new TimeoutException();
        Assert.Contains("may still be carried out", await TeamsReplyAsync(client, TeamsRequest("approve a1", "m-1")));
        store.Fail = null;
        Assert.Contains("already handled", await TeamsReplyAsync(client, TeamsRequest("approve a1", "m-1")));

        // A failure: nothing changed, so the same message can be sent again.
        store.Fail = new InvalidOperationException();
        Assert.Contains("nothing was changed", await TeamsReplyAsync(client, TeamsRequest("approve a2", "m-2")));
        store.Fail = null;
        Assert.Contains("not pending", await TeamsReplyAsync(client, TeamsRequest("approve a2", "m-2")));
    }

    // ---------------------------------------------------------------- health check

    [Fact]
    public async Task A_store_outage_is_reported_as_degraded_even_with_an_unhealthy_attention_status()
    {
        var store = new ScriptedStore { Fail = new InvalidOperationException("store down") };
        await using var host = await HomeostatTestHost.StartAsync(
            h => h.UseStore(_ => store),
            services => services.AddHealthChecks().AddHomeostatCheck(attentionStatus: HealthStatus.Unhealthy));

        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();

        Assert.Equal(HealthStatus.Degraded, report.Entries["homeostat"].Status);
        Assert.Equal("The Homeostat store is unavailable.", report.Entries["homeostat"].Description);
    }

    // ---------------------------------------------------------------- Polly: several pipelines per source

    [Fact]
    public async Task A_circuit_breaker_source_reports_the_worst_state_of_its_pipelines_and_drops_disposed_ones()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHomeostat().AddCircuitBreakerSignals();
        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<CircuitBreakerRegistry>();

        var control = new CircuitBreakerManualControl();
        await using var pipelines = new ResiliencePipelineRegistry<string>();
        foreach (var authority in new[] { "radar-a", "radar-b" })
        {
            pipelines.TryAddBuilder(authority, (pipeline, _) => pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                StateProvider = provider.TrackCircuitBreaker("radar-circuit"),
                ManualControl = authority == "radar-a" ? control : null,
            }));
            _ = pipelines.GetPipeline(authority);
        }

        Assert.Equal([("radar-circuit", CircuitState.Closed)], registry.States().ToArray());
        await control.IsolateAsync();
        Assert.Equal([("radar-circuit", CircuitState.Isolated)], registry.States().ToArray());   // one of two is enough

        await pipelines.DisposeAsync();
        Assert.Empty(registry.States());
        Assert.Equal(0, registry.Count("radar-circuit"));

        // Pipelines rebuilt without being disposed cannot grow the list without bound.
        for (var i = 0; i < CircuitBreakerRegistry.MaxProvidersPerSource + 10; i++)
        {
            provider.TrackCircuitBreaker("forecast-circuit");
        }

        Assert.Equal(CircuitBreakerRegistry.MaxProvidersPerSource, registry.Count("forecast-circuit"));
    }

    // ---------------------------------------------------------------- feature filter warnings

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task Invalid_switch_filter_parameters_are_warned_about_once_per_feature()
    {
        var logger = new CollectingLogger<HomeostatSwitchFilter>();
        var services = new ServiceCollection().AddLogging();
        services.AddHomeostat();
        await using var provider = services.BuildServiceProvider();
        var filter = new HomeostatSwitchFilter(provider.GetRequiredService<IHomeostatSwitches>(), logger);
        var missing = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var wrong = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Switch"] = "s", ["EnabledWhen"] = "Sometimes" }).Build();

        foreach (var (feature, parameters) in new[] { ("ForecastMaps", missing), ("ForecastMaps", missing), ("RadarLoop", wrong), ("RadarLoop", wrong) })
        {
            Assert.True(await filter.EvaluateAsync(new Microsoft.FeatureManagement.FeatureFilterEvaluationContext { FeatureName = feature, Parameters = parameters }));
        }

        Assert.Equal(2, logger.Messages.Count);
        Assert.Contains(logger.Messages, m => m.Contains("ForecastMaps", StringComparison.Ordinal) && m.Contains("'Switch'", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, m => m.Contains("RadarLoop", StringComparison.Ordinal) && m.Contains("Sometimes", StringComparison.Ordinal));
    }
}
