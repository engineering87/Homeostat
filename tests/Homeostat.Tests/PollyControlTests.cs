using Homeostat.Polly;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using Polly.Simmy;
using Polly.Simmy.Fault;

namespace Homeostat.Tests;

public class PollyControlTests
{
    private static ServiceProvider Services()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddHomeostat(h => h.AddCircuitBreakerSignals());
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Circuit_control_isolates_and_closes_with_its_switch()
    {
        await using var services = Services();
        var switches = services.GetRequiredService<IHomeostatSwitches>();
        var pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions { ManualControl = services.CircuitControl("forecast") })
            .Build();
        Assert.Same(services.CircuitControl("forecast"), services.CircuitControl("forecast"));

        Assert.Equal(1, pipeline.Execute(() => 1));

        switches.TurnOn("circuit:forecast:isolated");
        await WaitUntilAsync(() => Throws<IsolatedCircuitException>(pipeline));

        switches.TurnOff("circuit:forecast:isolated");
        await WaitUntilAsync(() => !Throws<IsolatedCircuitException>(pipeline));
    }

    [Fact]
    public async Task Control_created_while_the_switch_is_on_starts_isolated()
    {
        await using var services = Services();
        services.GetRequiredService<IHomeostatSwitches>().TurnOn("circuit:archive:isolated");

        var pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions { ManualControl = services.CircuitControl("archive") })
            .Build();

        Assert.True(Throws<IsolatedCircuitException>(pipeline));
    }

    [Fact]
    public async Task IsolateCircuit_step_isolates_until_the_condition_clears()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddCircuitBreakerSignals()
            .AddCondition("forecast-provider-down", c => c.ForSource("forecast-provider").Heal(p => p
                .IsolateCircuit("forecast")
                .VerifyWith<BuiltInActionTests.AlwaysHealed>())));

        host.Publish("forecast-provider", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("forecast-provider-down");

        Assert.Equal("Turn on switch 'circuit:forecast:isolated'", run.Steps.Single().Name);
        Assert.True(host.Switches.IsOn("circuit:forecast:isolated"));

        host.Publish("forecast-provider", SignalSeverity.Healthy);
        await WaitUntilAsync(() => !host.Switches.IsOn("circuit:forecast:isolated"));
    }

    [Fact]
    public async Task Chaos_is_injected_only_while_its_switch_is_on()
    {
        await using var services = Services();
        var switches = services.GetRequiredService<IHomeostatSwitches>();
        var pipeline = new ResiliencePipelineBuilder()
            .AddChaosFault(new ChaosFaultStrategyOptions
            {
                InjectionRate = 1,
                FaultGenerator = static _ => ValueTask.FromResult<Exception?>(new TimeoutException("chaos")),
                EnabledGenerator = services.ChaosEnabledBySwitch("chaos:forecast"),
            })
            .Build();

        Assert.Equal(1, pipeline.Execute(() => 1));
        switches.TurnOn("chaos:forecast");
        Assert.Throws<TimeoutException>(() => pipeline.Execute(() => 1));
        switches.TurnOff("chaos:forecast");
        Assert.Equal(1, pipeline.Execute(() => 1));
    }

    [Fact]
    public async Task Chaos_stops_by_itself_after_its_maximum_duration()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.Parse("2026-11-04T10:00:00Z"));
        var collection = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(time);
        collection.AddHomeostat(h => h.AddCircuitBreakerSignals());
        await using var services = collection.BuildServiceProvider();
        var switches = services.GetRequiredService<IHomeostatSwitches>();
        ResiliencePipeline Pipeline() => new ResiliencePipelineBuilder()
            .AddChaosFault(new ChaosFaultStrategyOptions
            {
                InjectionRate = 1,
                FaultGenerator = static _ => ValueTask.FromResult<Exception?>(new TimeoutException("chaos")),
                EnabledGenerator = services.ChaosEnabledBySwitch("chaos:geocoder", TimeSpan.FromMinutes(20)),
            })
            .Build();

        var pipeline = Pipeline();
        switches.TurnOn("chaos:geocoder");
        Assert.Throws<TimeoutException>(() => pipeline.Execute(() => 1));
        time.Advance(TimeSpan.FromMinutes(19));
        Assert.Throws<TimeoutException>(() => Pipeline().Execute(() => 1));   // a pipeline built again keeps the clock

        // Nobody ended the game day: chaos stops, though the switch is still on.
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, pipeline.Execute(() => 1));
        Assert.True(switches.IsOn("chaos:geocoder"));

        // Off and on again: another round.
        switches.TurnOff("chaos:geocoder");
        Assert.Equal(1, pipeline.Execute(() => 1));
        time.Advance(TimeSpan.FromSeconds(1));   // decisions are ordered by time: a turn-on does not win a tie
        switches.TurnOn("chaos:geocoder");
        Assert.Throws<TimeoutException>(() => pipeline.Execute(() => 1));

        // Off and on again with no call in between: still another round.
        time.Advance(TimeSpan.FromMinutes(21));
        Assert.Equal(1, pipeline.Execute(() => 1));
        switches.TurnOff("chaos:geocoder");
        time.Advance(TimeSpan.FromSeconds(1));
        switches.TurnOn("chaos:geocoder");
        Assert.Throws<TimeoutException>(() => pipeline.Execute(() => 1));
    }

    [Fact]
    public void Switch_changes_raise_events_only_on_change_and_survive_failing_handlers()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddHomeostat();
        using var provider = services.BuildServiceProvider();
        var switches = provider.GetRequiredService<IHomeostatSwitches>();
        var events = new List<string>();
        switches.Changed += (_, _) => throw new InvalidOperationException("broken subscriber");
        switches.Changed += (_, e) => events.Add($"{e.Name}={e.IsOn}");

        switches.TurnOn("degrade");
        switches.TurnOn("degrade");
        switches.TurnOff("degrade");
        switches.TurnOff("degrade");

        Assert.Equal(["degrade=True", "degrade=False"], events);
    }

    private static bool Throws<TException>(ResiliencePipeline pipeline)
        where TException : Exception
    {
        try
        {
            pipeline.Execute(() => 1);
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }
}
