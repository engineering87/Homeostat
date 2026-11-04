using Homeostat.Notifications;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Homeostat.Tests;

public class CustomSinkTests
{
    [Fact]
    public async Task A_custom_sink_receives_the_selected_events()
    {
        var store = new IncidentStore();
        await using (var host = await HomeostatTestHost.StartAsync(
            h => h
                .AddCondition("feed-down", c => c
                    .ForSource("feed")
                    .Heal(p => p.With("renew", _ => { }).VerifyWith<EngineTests.AlwaysHealedVerifier>()))
                .AddEventSink<IncidentSink>()
                .AddEventSink<IncidentSink>(),
            services => services.AddSingleton(store)))
        {
            host.Publish("feed", SignalSeverity.Unhealthy);
            await host.WaitForRunAsync("feed-down");
        }

        var summary = Assert.Single(store.Summaries);
        Assert.Contains("Healed", summary);
    }

    internal sealed class IncidentStore
    {
        private readonly object _gate = new();
        private readonly List<string> _summaries = [];

        public IReadOnlyList<string> Summaries { get { lock (_gate) { return _summaries.ToArray(); } } }

        public Task AddAsync(string summary)
        {
            lock (_gate)
            {
                _summaries.Add(summary);
            }

            return Task.CompletedTask;
        }
    }

    internal sealed class IncidentSink(IncidentStore store) : IHomeostatEventSink
    {
        public IReadOnlyCollection<HomeostatEventKind>? Events => [HomeostatEventKind.RunCompleted];

        public Task WriteAsync(HomeostatEvent e, CancellationToken cancellationToken) => store.AddAsync(e.Summary);
    }
}
