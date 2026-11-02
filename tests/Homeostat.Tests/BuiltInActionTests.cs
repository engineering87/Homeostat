using Homeostat.Conditions;
using Homeostat.Healing;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Homeostat.Tests;

public class BuiltInActionTests
{
    private const string Condition = "feed-down";
    private const string Source = "feed";

    private static Task<HomeostatTestHost> StartAsync(Action<HealingPlanBuilder> plan, Action<IServiceCollection>? services = null)
        => HomeostatTestHost.StartAsync(
            h => h.AddCondition(Condition, c => c.ForSource(Source).Heal(p => plan(p.VerifyWith<AlwaysHealed>()))),
            services);

    [Fact]
    public async Task Inline_action_receives_the_step_services()
    {
        var calls = 0;
        await using var host = await StartAsync(
            p => p.With("count", sp => sp.GetRequiredService<Counter>().Increment()),
            s => s.AddSingleton(new Counter(() => Interlocked.Increment(ref calls))));

        host.Publish(Source, SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync(Condition);

        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Equal("count", run.Steps.Single().Name);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Async_inline_action_is_awaited_before_the_step_completes()
    {
        var finished = false;
        await using var host = await StartAsync(p => p.With("slow", async _ =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            Volatile.Write(ref finished, true);
        }).ActionTimeout(TimeSpan.FromMinutes(10)));

        host.Publish(Source, SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync(Condition);

        Assert.True(run.Steps.Single().Succeeded);
        Assert.True(Volatile.Read(ref finished));
    }

    [Fact]
    public async Task TurnOnSwitch_turns_the_switch_on()
    {
        await using var host = await StartAsync(p => p.TurnOnSwitch("serve-cached-forecast"));
        Assert.False(host.Switches.IsOn("serve-cached-forecast"));

        host.Publish(Source, SignalSeverity.Unhealthy);
        await host.WaitForRunAsync(Condition);

        Assert.True(host.Switches.IsOn("serve-cached-forecast"));
        Assert.Contains("serve-cached-forecast", host.Switches.GetActive());
    }

    [Fact]
    public async Task RestartHostedService_stops_and_starts_the_service()
    {
        await using var host = await StartAsync(
            p => p.RestartHostedService<CountingService>(),
            s => s.AddHostedService<CountingService>());

        var service = host.Services.GetServices<IHostedService>().OfType<CountingService>().Single();
        Assert.Equal(1, service.Starts);

        host.Publish(Source, SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync(Condition);

        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Equal(1, service.Stops);
        Assert.Equal(2, service.Starts);
    }

    [Fact]
    public async Task RestartHostedService_fails_the_step_when_the_service_is_not_registered()
    {
        await using var host = await StartAsync(p => p.RestartHostedService<CountingService>());

        host.Publish(Source, SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync(Condition);

        Assert.Equal(HealingRunOutcome.NotHealed, run.Outcome);
        Assert.IsType<InvalidOperationException>(run.Steps.Single().Error);
    }

    [Fact]
    public async Task ReloadConfiguration_reloads_the_configuration_root()
    {
        var source = new ReloadCountingSource();
        IConfiguration configuration = new ConfigurationBuilder().Add(source).Build();

        await using var host = await StartAsync(p => p.ReloadConfiguration(), s => s.AddSingleton(configuration));
        var loadsBefore = source.Provider.Loads;

        host.Publish(Source, SignalSeverity.Unhealthy);
        await host.WaitForRunAsync(Condition);

        Assert.Equal(loadsBefore + 1, source.Provider.Loads);
    }

    [Fact]
    public async Task Observers_receive_condition_step_and_run_events()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddObserver<CollectingObserver>()
            .AddCondition(Condition, c => c.ForSource(Source).Heal(p => p.With("noop", _ => { }).VerifyWith<AlwaysHealed>())));

        host.Publish(Source, SignalSeverity.Unhealthy);
        await host.WaitForRunAsync(Condition);
        host.Publish(Source, SignalSeverity.Healthy);
        await host.WaitForConditionAsync(Condition, ConditionStatus.Inactive);

        var observer = host.Services.GetServices<IHealingObserver>().OfType<CollectingObserver>().Single();
        await WaitUntilAsync(() => observer.Events.Contains("cleared:" + Condition));
        Assert.Equal(
            new[] { "activated:" + Condition, "step:noop", "run:Healed", "cleared:" + Condition },
            observer.Events);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 200 && !predicate(); i++)
        {
            await Task.Delay(10);
        }
    }

    internal sealed class Counter(Action increment)
    {
        public void Increment() => increment();
    }

    internal sealed class AlwaysHealed : IHealingVerifier
    {
        public Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(VerificationOutcome.Healed);
    }

    internal sealed class CountingService : BackgroundService
    {
        private int _starts;
        private int _stops;

        public int Starts => Volatile.Read(ref _starts);

        public int Stops => Volatile.Read(ref _stops);

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _starts);
            return base.StartAsync(cancellationToken);
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stops);
            return base.StopAsync(cancellationToken);
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
            => Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    internal sealed class CollectingObserver : IHealingObserver
    {
        private readonly object _gate = new();
        private readonly List<string> _events = [];

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

        public void OnConditionActivated(ConditionSnapshot condition) => Add("activated:" + condition.Name);

        public void OnConditionCleared(ConditionSnapshot condition) => Add("cleared:" + condition.Name);

        public void OnStepCompleted(HealingStepResult step) => Add("step:" + step.Name);

        public void OnRunCompleted(HealingRunResult run) => Add("run:" + run.Outcome);

        private void Add(string value)
        {
            lock (_gate)
            {
                _events.Add(value);
            }
        }
    }

    internal sealed class ReloadCountingSource : IConfigurationSource
    {
        public ReloadCountingProvider Provider { get; } = new();

        public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
    }

    internal sealed class ReloadCountingProvider : ConfigurationProvider
    {
        private int _loads;

        public int Loads => Volatile.Read(ref _loads);

        public override void Load() => Interlocked.Increment(ref _loads);
    }
}
