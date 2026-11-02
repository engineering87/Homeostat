using System.Net;
using Homeostat.Conditions;
using Homeostat.Polly;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;

namespace Homeostat.Tests;

public class PollyAndTestingTests
{
    [Theory]
    [InlineData(CircuitState.Closed, SignalSeverity.Healthy)]
    [InlineData(CircuitState.HalfOpen, SignalSeverity.Degraded)]
    [InlineData(CircuitState.Open, SignalSeverity.Unhealthy)]
    [InlineData(CircuitState.Isolated, SignalSeverity.Unhealthy)]
    public void Circuit_states_map_to_severities(CircuitState state, SignalSeverity expected)
        => Assert.Equal(expected, CircuitBreakerSignalDetector.Map(state));

    [Fact]
    public void Tracking_requires_circuit_breaker_signals_to_be_enabled()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => provider.TrackCircuitBreaker("forecast-circuit"));
    }

    [Fact]
    public void Tracking_returns_a_new_provider_each_time()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHomeostat().AddCircuitBreakerSignals();
        using var provider = services.BuildServiceProvider();

        var first = provider.TrackCircuitBreaker("forecast-circuit");
        var second = provider.TrackCircuitBreaker("forecast-circuit");

        Assert.NotSame(first, second);
    }

    [Fact]
    public void HealingContextFactory_creates_a_usable_context()
    {
        var context = HealingContextFactory.Create(conditionName: "feed-down", stepName: "renew");

        Assert.Equal("feed-down", context.ConditionName);
        Assert.Equal("renew", context.StepName);
        Assert.Equal(ConditionStatus.Active, context.GetConditionState().Status);
        Assert.NotNull(context.Services);
    }

    [Fact]
    public async Task FaultInjectingHandler_returns_the_configured_status_while_active()
    {
        var fault = new FaultSwitch();
        using var client = new HttpClient(new FaultInjectingHandler(fault, HttpStatusCode.BadGateway) { InnerHandler = new OkHandler() });

        using (var ok = await client.GetAsync("http://dependency.test/"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        fault.Activate();
        using var failed = await client.GetAsync("http://dependency.test/");
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request });
    }
}
