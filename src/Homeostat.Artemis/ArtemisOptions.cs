using Homeostat.Probes;

namespace Homeostat.Artemis;

/// <summary>Where and how to reach the management API (Jolokia) of an ActiveMQ Artemis broker.</summary>
public sealed class ArtemisOptions
{
    /// <summary>Address of the broker's web console, for example <c>http://artemis:8161</c>. Required.</summary>
    public Uri? ManagementUrl { get; set; }

    /// <summary>
    /// Path of the Jolokia endpoint, appended to the path of <see cref="ManagementUrl"/> (so a console behind a reverse
    /// proxy prefix works); defaults to <c>/console/jolokia</c>.
    /// </summary>
    public string JolokiaPath { get; set; } = "/console/jolokia";

    /// <summary>
    /// Name of the broker, as in <c>broker.xml</c> (<c>&lt;name&gt;</c>) and in the MBean names, for example
    /// <c>0.0.0.0</c> in the default configuration. Required.
    /// </summary>
    public string? BrokerName { get; set; }

    /// <summary>User of the management API (a role allowed in <c>management.xml</c>), if it requires one.</summary>
    public string? UserName { get; set; }

    /// <summary>Password of <see cref="UserName"/>.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// Value of the <c>Origin</c> header. Artemis checks it against <c>jolokia-access.xml</c> (by default only
    /// <c>localhost</c> origins are allowed): allow this origin there. Defaults to the origin of <see cref="ManagementUrl"/>.
    /// </summary>
    public string? Origin { get; set; }

    internal void Validate()
    {
        if (ManagementUrl is not { IsAbsoluteUri: true } url || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("ArtemisOptions.ManagementUrl must be an absolute http or https address, for example http://artemis:8161.");
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            throw new ArgumentException("Put the credentials in ArtemisOptions.UserName and Password, not in ManagementUrl.");
        }

        if (string.IsNullOrWhiteSpace(BrokerName))
        {
            throw new ArgumentException("ArtemisOptions.BrokerName is required: the <name> of the broker in broker.xml.");
        }

        if (UserName is not null && Password is null)
        {
            throw new ArgumentException("ArtemisOptions.Password is required with a UserName.");
        }
    }
}

/// <summary>Settings of the signals of an Artemis queue.</summary>
public sealed class ArtemisQueueSignalOptions
{
    /// <summary>Address the queue is bound to; defaults to the queue name (the usual point-to-point setup).</summary>
    public string? Address { get; set; }

    /// <summary>Routing type of the queue: <c>anycast</c> (point-to-point, the default) or <c>multicast</c> (a subscription).</summary>
    public string RoutingType { get; set; } = "anycast";

    /// <summary>
    /// Source prefix of the signals: <c>{prefix}:messages</c> and <c>{prefix}:consumers</c> (and <c>{prefix}:dead-letter</c>).
    /// Defaults to <c>artemis:{queue}</c>.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>How often the queue is read; defaults to 30 seconds.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Thresholds of the message count (the backlog, including messages being delivered).</summary>
    public ProbeOptions Messages { get; } = new();

    /// <summary>
    /// Thresholds of the consumer count. By default a queue without consumers is Unhealthy (<c>UnhealthyBelow = 1</c>);
    /// set it to <c>null</c> when consumers can be absent, for example while paused.
    /// </summary>
    public ProbeOptions Consumers { get; } = new() { UnhealthyBelow = 1 };

    /// <summary>
    /// A dead-letter queue to watch as well, published as <c>{prefix}:dead-letter</c>. In the default configuration it
    /// is the queue <c>DLQ</c>, shared by every address, so the count is broker-wide; with
    /// <c>auto-create-dead-letter-resources</c> each address gets its own, <c>DLQ.{address}</c> (the default prefix).
    /// Watch the count with <c>WhenRisingFasterThan</c>.
    /// </summary>
    public string? DeadLetterQueue { get; set; }

    /// <summary>Address of <see cref="DeadLetterQueue"/>; defaults to <c>DLQ</c>, the default dead-letter address.</summary>
    public string DeadLetterAddress { get; set; } = "DLQ";

    /// <summary>Thresholds of the dead-letter count.</summary>
    public ProbeOptions DeadLetter { get; } = new();
}
