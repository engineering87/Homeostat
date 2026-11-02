using Homeostat.Healing;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Homeostat.Tests;

public class RestartApplicationTests
{
    private static Task<HomeostatTestHost> StartAsync(FakeLifetime lifetime, Action<HealingPlanBuilder> plan)
        => HomeostatTestHost.StartAsync(
            h => h.AddCondition("leak", c => c.ForSource("memory").Heal(p => plan(p.VerifyWith<BuiltInActionTests.AlwaysHealed>()))),
            s => s.AddSingleton<IHostApplicationLifetime>(lifetime));

    [Fact]
    public async Task Restart_waits_for_approval_then_stops_the_application_with_the_exit_code()
    {
        var lifetime = new FakeLifetime();
        var previous = Environment.ExitCode;
        try
        {
            await using var host = await StartAsync(lifetime, p => p.RestartApplication(exitCode: 75));
            host.Time.Advance(TimeSpan.FromMinutes(11));

            host.Publish("memory", SignalSeverity.Unhealthy);
            await PollyControlTests.WaitUntilAsync(() => host.Approvals.GetPending().Count == 1);
            Assert.False(lifetime.Stopped);                  // approval is on by default

            host.Approvals.Approve(host.Approvals.GetPending()[0].Id, "ops");
            await PollyControlTests.WaitUntilAsync(() => lifetime.Stopped);
            Assert.Equal(75, Environment.ExitCode);
        }
        finally
        {
            Environment.ExitCode = previous;
        }
    }

    [Fact]
    public async Task Restart_is_refused_right_after_startup()
    {
        var lifetime = new FakeLifetime();
        await using var host = await StartAsync(lifetime, p => p.RestartApplication(requireApproval: false));
        host.Publish("memory", SignalSeverity.Unhealthy);

        var run = await host.WaitForRunAsync("leak");

        Assert.Equal(HealingRunOutcome.NotHealed, run.Outcome);
        Assert.Contains("minimum uptime", run.Steps.Single().Error!.Message);
        Assert.False(lifetime.Stopped);
    }

    [Fact]
    public async Task Restarts_are_limited_per_window_across_processes()
    {
        var store = new Homeostat.Persistence.InMemoryHomeostatStore();   // shared, like a file or Redis store
        var limits = new RestartLimits(TimeSpan.Zero, MaxRestarts: 2, TimeSpan.FromHours(1));
        var previous = Environment.ExitCode;
        try
        {
            for (var process = 1; process <= 3; process++)
            {
                var lifetime = new FakeLifetime();
                await using var host = await HomeostatTestHost.StartAsync(
                    h => h
                        .UseStore(_ => store)
                        .AddCondition("leak", c => c.ForSource("memory").Heal(p => p
                            .RestartApplication(requireApproval: false, limits: limits)
                            .Cooldown(TimeSpan.Zero)                     // the shared store also carries the cooldown: isolate the limit
                            .VerifyWith<BuiltInActionTests.AlwaysHealed>())),
                    s => s.AddSingleton<IHostApplicationLifetime>(lifetime));

                host.Publish("memory", SignalSeverity.Unhealthy);
                var run = await host.WaitForRunAsync("leak");

                if (process <= 2)
                {
                    Assert.True(lifetime.Stopped, $"process {process} should restart");
                }
                else
                {
                    Assert.False(lifetime.Stopped);
                    Assert.Contains("already restarted 2 times", run.Steps.Single().Error!.Message);
                }
            }
        }
        finally
        {
            Environment.ExitCode = previous;
        }
    }

    internal sealed class FakeLifetime : IHostApplicationLifetime
    {
        private volatile bool _stopped;

        public bool Stopped => _stopped;

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopped = true;
    }
}
