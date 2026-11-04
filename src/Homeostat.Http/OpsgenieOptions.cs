namespace Homeostat.Http;

/// <summary>
/// Settings of the Opsgenie notifier, which uses the Opsgenie alert API. That API is served by Jira Service Management
/// Operations (the default endpoint) and by standalone Opsgenie, which Atlassian supports until 5 April 2027.
/// </summary>
public sealed class OpsgenieOptions : IncidentOptions
{
    /// <summary>Name of the <see cref="HttpClient"/> used to send events; configure it (proxy, handlers) with <c>AddHttpClient(OpsgenieOptions.HttpClientName)</c>.</summary>
    public const string HttpClientName = "Homeostat.Opsgenie";

    /// <summary>Jira Service Management Operations alert API.</summary>
    public static readonly Uri JiraServiceManagement = new("https://api.atlassian.com/jsm/ops/integration/v2/alerts");

    /// <summary>Standalone Opsgenie alert API (US).</summary>
    public static readonly Uri Opsgenie = new("https://api.opsgenie.com/v2/alerts");

    /// <summary>Standalone Opsgenie alert API (EU).</summary>
    public static readonly Uri OpsgenieEu = new("https://api.eu.opsgenie.com/v2/alerts");

    /// <summary>API key of the API integration. Required; keep it in a secret store.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Alert API endpoint. Defaults to <see cref="JiraServiceManagement"/>.</summary>
    public Uri Url { get; set; } = JiraServiceManagement;

    /// <summary>Scheme of the <c>Authorization</c> header sent with the API key. Defaults to <c>GenieKey</c>.</summary>
    public string AuthorizationScheme { get; set; } = "GenieKey";

    /// <summary>Tags added to every alert. Defaults to <c>homeostat</c>.</summary>
    public ISet<string> Tags { get; } = new HashSet<string>(StringComparer.Ordinal) { "homeostat" };

    internal override void Validate()
    {
        base.Validate();
        if (string.IsNullOrWhiteSpace(ApiKey) || Url is not { IsAbsoluteUri: true } || string.IsNullOrWhiteSpace(AuthorizationScheme))
        {
            throw new ArgumentException("OpsgenieOptions.ApiKey and AuthorizationScheme are required and Url must be absolute.");
        }
    }
}
