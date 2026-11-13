namespace Homeostat.Samples.Architectures.ModularMonolith;

/// <summary>
/// What the team knows about the process the modules share, written down for Homeostat: the global limits and the
/// plan for process-wide pressure. The reasoning behind each line is in docs/architecture-examples.md.
/// </summary>
public static class ProcessHealing
{
    /// <summary>The healing group of the process-wide conditions, which belong to no module.</summary>
    public const string Group = "process";

    /// <summary>Adds the global limits and the plan for a starved thread pool.</summary>
    public static HomeostatBuilder AddProcessHealing(this HomeostatBuilder h) => h
        // Every module heals inside these limits: 10 actions per hour and 3 runs at a time for the whole process.
        .Configure(o =>
        {
            o.MaxActionsPerWindow = 10;
            o.MaxConcurrentHealings = 3;
        })
        // One complete shed per hour at most.
        .ConfigureGroup(Group, g => g.MaxActionsPerWindow = 2)

        // The modules share the thread pool: when it starves, every module slows down, alerts included. Homeostat
        // cannot tell which module causes it; the order of the steps says whose work goes first.
        .AddCondition("threadpool-starved", c => c
            .ForSource("threadpool")
            .InGroup(Group)
            .SustainedFor(TimeSpan.FromMinutes(1))
            .ClearAfter(TimeSpan.FromMinutes(5))
            .Heal(p => p
                // Reports are deferrable: new exports wait, the running ones finish.
                .TurnOnSwitch(ReportsHealing.Paused)
                // Then tiles render at a lower resolution, with less CPU. No step touches alerts.
                .TurnOnSwitch(TileRenderer.LowResolution)));
}
