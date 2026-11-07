namespace Homeostat.Azure;

/// <summary>How Homeostat calls Azure Resource Manager.</summary>
public sealed class AzureOptions
{
    /// <summary>Azure public cloud Resource Manager endpoint.</summary>
    public static readonly Uri PublicCloud = new("https://management.azure.com/");

    /// <summary>
    /// Obtains an access token for Resource Manager. Required. With Azure.Identity and a managed identity:
    /// <c>o.AccessToken = async (services, ct) => (await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), ct)).Token</c>.
    /// The credential library caches and refreshes tokens; this delegate is called for every request.
    /// </summary>
    public Func<IServiceProvider, CancellationToken, ValueTask<string>>? AccessToken { get; set; }

    /// <summary>Resource Manager endpoint; change it for sovereign clouds (for example <c>https://management.usgovcloudapi.net/</c>).</summary>
    public Uri ResourceManager { get; set; } = PublicCloud;

    /// <summary>Timeout of one request. Defaults to 60 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}
