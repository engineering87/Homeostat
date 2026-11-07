namespace Homeostat.Http;

/// <summary>Settings of the PagerDuty notifier (Events API v2).</summary>
public sealed class PagerDutyOptions : IncidentOptions
{
    /// <summary>Name of the <see cref="HttpClient"/> used to send events; configure it (proxy, handlers) with <c>AddHttpClient(PagerDutyOptions.HttpClientName)</c>.</summary>
    public const string HttpClientName = "Homeostat.PagerDuty";

    /// <summary>Integration key of the PagerDuty service (Events API v2 integration). Required; keep it in a secret store.</summary>
    public string? RoutingKey { get; set; }

    /// <summary>Events API endpoint. Defaults to <c>https://events.pagerduty.com/v2/enqueue</c> (use <c>events.eu.pagerduty.com</c> for EU accounts).</summary>
    public Uri Url { get; set; } = new("https://events.pagerduty.com/v2/enqueue");

    /// <summary>Optional <c>component</c> field, for example the service name.</summary>
    public string? Component { get; set; }

    /// <summary>Optional <c>group</c> field, for example the team or the environment.</summary>
    public string? Group { get; set; }

    internal override void Validate()
    {
        base.Validate();
        if (string.IsNullOrWhiteSpace(RoutingKey) || Url is not { IsAbsoluteUri: true })
        {
            throw new ArgumentException("PagerDutyOptions.RoutingKey is required and Url must be absolute.");
        }
    }
}
