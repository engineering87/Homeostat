using Homeostat.Probes;
using Azure.Messaging.ServiceBus.Administration;

namespace Homeostat.ServiceBus;

/// <summary>Settings of the signals of a Service Bus queue or subscription.</summary>
public sealed class ServiceBusSignalOptions
{
    /// <summary>
    /// Source prefix of the signals: <c>{prefix}:active</c> (messages waiting) and <c>{prefix}:dead-letter</c>.
    /// Defaults to <c>servicebus:{queue}</c> or <c>servicebus:{topic}/{subscription}</c>.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>How often the counts are read; defaults to 30 seconds (Service Bus updates them about that often).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Thresholds of the active message count (the backlog), for example <c>UnhealthyAbove = 10_000</c>.</summary>
    public ProbeOptions Active { get; } = new();

    /// <summary>
    /// Thresholds of the dead-letter count. Dead letters accumulate until someone handles them, so a growing count
    /// matters more than its level: watch it with <c>WhenRisingFasterThan</c>.
    /// </summary>
    public ProbeOptions DeadLetter { get; } = new();

    /// <summary>
    /// Returns the administration client; by default the <see cref="ServiceBusAdministrationClient"/> registered in the
    /// services. It needs the <c>Manage</c> right (or the Azure Service Bus Data Owner role) to read the counts.
    /// </summary>
    public Func<IServiceProvider, ServiceBusAdministrationClient>? Client { get; set; }
}
