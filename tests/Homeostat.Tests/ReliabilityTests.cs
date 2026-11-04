using System.Diagnostics;
using System.Net;
using Homeostat.Healing;
using Homeostat.Http;
using Homeostat.Persistence;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Homeostat.Tests;

/// <summary>
/// Homeostat must never be what takes the application down: a store that never answers, an action or a verifier
/// that never returns, a classifier that throws. Each test breaks one piece and checks that the application keeps
/// starting, serving, healing what it can and stopping on time.
/// </summary>
public class ReliabilityTests
{
    [Fact]
    public async Task A_store_that_never_answers_does_not_block_startup_health_checks_or_shutdown()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.Services.AddHomeostat(h => h
            .UseStore(_ => new SilentStore())
            .Configure(o => o.DryRun = false)
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p.With("renew", _ => { }))));
        builder.Services.AddHealthChecks().AddHomeostatCheck();
        using var host = builder.Build();

        var watch = Stopwatch.StartNew();
        await host.StartAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"startup took {watch.Elapsed}");

        // Publishing never waits, whatever the store does.
        var publisher = host.Services.GetRequiredService<ISignalPublisher>();
        watch.Restart();
        for (var i = 0; i < 100_000; i++)
        {
            Assert.True(publisher.TryPublish(new Signal("feed", SignalSeverity.Unhealthy, DateTimeOffset.UtcNow)));
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"publishing took {watch.Elapsed}");

        // The control state could not be read: Homeostat does not act, and says why.
        var control = host.Services.GetRequiredService<IHomeostatControl>();
        Assert.True(control.IsPaused);
        Assert.StartsWith("waiting for the control state", control.PauseReason);

        // The health check answers in time (a liveness probe that times out would restart the application).
        watch.Restart();
        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the health check took {watch.Elapsed}");
        Assert.Equal(HealthStatus.Degraded, report.Entries["homeostat"].Status);

        watch.Restart();
        await host.StopAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"shutdown took {watch.Elapsed}");
    }

    [Fact]
    public async Task An_action_that_ignores_cancellation_does_not_hold_the_concurrency_slot()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .Configure(o => o.MaxConcurrentHealings = 1)
            .AddCondition("feed-down", c => c.ForSource("feed")
                .Heal(p => p.With("stuck", (_, _) => new TaskCompletionSource().Task).ActionTimeout(TimeSpan.FromSeconds(30))))
            .AddCondition("disk-full", c => c.ForSource("disk")
                .Heal(p => p.With("prune", _ => { }).VerifyWith<EngineTests.AlwaysHealedVerifier>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var stuck = await host.WaitForRunAsync("feed-down");
        Assert.IsType<TimeoutException>(Assert.Single(stuck.Steps).Error);

        // The slot was released: another condition still heals.
        host.Publish("disk", SignalSeverity.Unhealthy);
        Assert.Equal(HealingRunOutcome.Healed, (await host.WaitForRunAsync("disk-full")).Outcome);
    }

    [Fact]
    public async Task A_verifier_that_never_returns_ends_the_run_as_inconclusive()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("feed-down", c => c.ForSource("feed")
                .Heal(p => p.With("renew", _ => { }).VerificationTimeout(TimeSpan.FromSeconds(30)).VerifyWith<SilentVerifier>())));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.Equal(HealingRunOutcome.Inconclusive, run.Outcome);
        Assert.IsType<TimeoutException>(Assert.Single(run.Steps).Error);
    }

    [Fact]
    public async Task A_failure_classifier_that_throws_never_fails_the_application_call()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHomeostat();
        services.AddHttpClient("forecast")
            .ConfigurePrimaryHttpMessageHandler(() => new OkHandler())
            .AddHomeostatSignals(o => o.IsFailure = _ => throw new InvalidOperationException("broken classifier"));
        await using var provider = services.BuildServiceProvider();

        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("forecast").GetAsync("http://forecast.test/today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Recording too: the caller gets its own exception, not the classifier's.
        var recorder = new Calls.CallRecorder("geocoder");
        var error = await Assert.ThrowsAsync<IOException>(() => recorder.TrackAsync(
            _ => Task.FromException(new IOException("geocoder down")), _ => throw new InvalidOperationException("broken classifier")));
        Assert.Equal("geocoder down", error.Message);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    internal sealed class SilentVerifier : IHealingVerifier
    {
        public Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
            => new TaskCompletionSource<VerificationOutcome>().Task;
    }

    /// <summary>A store whose calls never complete and ignore their token, the worst a custom store can do.</summary>
    private sealed class SilentStore : IHomeostatStore
    {
        private static ValueTask<T> Never<T>() => new(new TaskCompletionSource<T>().Task);

        private static ValueTask Never() => new(new TaskCompletionSource().Task);

        public ValueTask<PlanState> GetPlanStateAsync(string key, CancellationToken cancellationToken = default) => Never<PlanState>();

        public ValueTask<PlanState> UpdatePlanStateAsync(string key, PlanStateUpdate update, CancellationToken cancellationToken = default) => Never<PlanState>();

        public ValueTask<bool> TryConsumeBudgetAsync(DateTimeOffset now, int limit, TimeSpan window, CancellationToken cancellationToken = default) => Never<bool>();

        public ValueTask<int> GetBudgetUsedAsync(DateTimeOffset now, TimeSpan window, CancellationToken cancellationToken = default) => Never<int>();

        public ValueTask AppendRunAsync(HealingRunRecord run, int maxRuns, CancellationToken cancellationToken = default) => Never();

        public ValueTask<IReadOnlyList<HealingRunRecord>> GetRecentRunsAsync(int maxRuns, CancellationToken cancellationToken = default) => Never<IReadOnlyList<HealingRunRecord>>();

        public ValueTask<ControlState> GetControlStateAsync(CancellationToken cancellationToken = default) => Never<ControlState>();

        public ValueTask SetPauseAsync(PauseState? pause, CancellationToken cancellationToken = default) => Never();

        public ValueTask SetSwitchAsync(string name, SwitchState? state, CancellationToken cancellationToken = default) => Never();

        public ValueTask<bool> TryClaimSwitchAsync(SwitchState state, CancellationToken cancellationToken = default) => Never<bool>();

        public ValueTask<bool> TryReleaseSwitchAsync(string name, string condition, string? instance, CancellationToken cancellationToken = default) => Never<bool>();

        public ValueTask AddApprovalAsync(ApprovalRecord approval, CancellationToken cancellationToken = default) => Never();

        public ValueTask<bool> TryResolveApprovalAsync(string id, ApprovalStatus status, string? resolvedBy, CancellationToken cancellationToken = default) => Never<bool>();

        public ValueTask RemoveApprovalAsync(string id, CancellationToken cancellationToken = default) => Never();
    }
}
