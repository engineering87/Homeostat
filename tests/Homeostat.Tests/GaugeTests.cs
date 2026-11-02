using System.Diagnostics.Metrics;
using Homeostat.Signals;
using Homeostat.Testing;

namespace Homeostat.Tests;

public class GaugeTests
{
    [Fact]
    public async Task Gauges_report_conditions_by_status_switches_and_pause()
    {
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("feed-down", c => c.ForSource("feed"))
            .AddCondition("cache-down", c => c.ForSource("cache")));
        host.Publish("feed", SignalSeverity.Unhealthy);
        await PollyControlTests.WaitUntilAsync(() => host.Control.GetConditions().Any(c => c.Status == Conditions.ConditionStatus.Active));
        host.Switches.TurnOn("serve-cached-forecast");
        host.Control.Pause();

        var values = new Dictionary<string, int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Homeostat" && instrument.Name is "homeostat.conditions" or "homeostat.switches.on" or "homeostat.paused" or "homeostat.approvals.pending")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            var status = tags.ToArray().FirstOrDefault(t => t.Key == "status").Value as string;
            lock (values)
            {
                // Several hosts may run at once in the test process: keep the largest value seen.
                var key = status is null ? instrument.Name : $"{instrument.Name}:{status}";
                values[key] = Math.Max(values.GetValueOrDefault(key), value);
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.True(values["homeostat.conditions:Active"] >= 1);
        Assert.True(values["homeostat.switches.on"] >= 1);
        Assert.Equal(1, values["homeostat.paused"]);
        Assert.True(values.ContainsKey("homeostat.approvals.pending"));
    }
}
