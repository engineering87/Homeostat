namespace Aspire.Hosting;

/// <summary>Settings of <c>WithHomeostat</c>.</summary>
public sealed class HomeostatResourceOptions
{
    /// <summary>Route prefix of the Homeostat endpoints in the application; defaults to <c>/homeostat</c>.</summary>
    public string Prefix { get; set; } = "/homeostat";

    /// <summary>Adds a link to the Homeostat dashboard (<c>MapHomeostatDashboard</c>) to the resource. Defaults to <c>true</c>.</summary>
    public bool DashboardLink { get; set; } = true;

    /// <summary>
    /// Adds the <b>Pause healing</b> and <b>Resume healing</b> commands, which call the control endpoints
    /// (<c>MapHomeostatControl</c>). Defaults to <c>true</c>.
    /// </summary>
    public bool Commands { get; set; } = true;

    /// <summary>How long <b>Pause healing</b> pauses for; <c>null</c> pauses until resumed. Defaults to 30 minutes.</summary>
    public TimeSpan? PauseFor { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Endpoint of the resource to call; by default its HTTPS endpoint, or else its HTTP endpoint.</summary>
    public string? EndpointName { get; set; }

    /// <summary>
    /// Returns a bearer token for the control endpoints, when their policy needs one; <c>null</c> sends none (for
    /// endpoints mapped with <c>allowAnonymous</c> in development).
    /// </summary>
    public Func<IServiceProvider, CancellationToken, Task<string?>>? BearerToken { get; set; }
}
