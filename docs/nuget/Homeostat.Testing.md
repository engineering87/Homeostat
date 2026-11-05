# Homeostat.Testing

Test helpers for Homeostat: an in-memory host that runs the real engine on simulated time, so a scenario with cooldowns and verification runs in milliseconds; run results and approvals to assert on; a `HealingContext` factory to unit test actions; HTTP fault injection; and replay of recorded signals against new conditions.

## Install

```bash
dotnet add package Homeostat.Testing --prerelease
```

The current version is a release candidate (`1.0.0-rc.1`): `--prerelease` is needed until 1.0.0 is published.

## Example

```csharp
using Homeostat;
using Homeostat.Healing;
using Homeostat.Signals;
using Homeostat.Testing;
using Xunit;

public sealed class FeedTests
{
    [Fact]
    public async Task Renewing_the_client_heals_the_feed()
    {
        var renewed = false;
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("feed-down", c => c
                .ForSource("feed")
                .Heal(p => p.With("renew", _ => renewed = true))));

        host.Publish("feed", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("feed-down");

        Assert.True(renewed);
        Assert.Equal(HealingRunOutcome.Inconclusive, run.Outcome);   // no signal after the action: no evidence either way
    }
}
```

## Documentation

[Testing](https://github.com/engineering87/Homeostat/blob/main/docs/testing.md)

This package extends [Homeostat](https://www.nuget.org/packages/Homeostat), the self-healing engine for .NET: conditions, healing plans, verification and guardrails. Start with its [getting started guide](https://github.com/engineering87/Homeostat/blob/main/docs/getting-started.md).

MIT licensed.
