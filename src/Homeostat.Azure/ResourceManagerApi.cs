using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Homeostat.Azure;

/// <summary>Azure Resource Manager over HTTPS, with the caller's token.</summary>
internal sealed partial class ResourceManagerApi
{
    internal const string HttpClientName = "Homeostat.Azure";
    private readonly IHttpClientFactory _clients;
    private readonly AzureOptions _options;

    public ResourceManagerApi(IHttpClientFactory clients, IOptions<AzureOptions> options)
    {
        _clients = clients;
        _options = options.Value;
    }

    /// <summary>
    /// Sends a request to <c>{resourceId}{action}?api-version={apiVersion}</c> and returns the response body, if any.
    /// Long-running operations answer 202: the request is accepted, and Homeostat's verification checks the outcome.
    /// </summary>
    public async Task<JsonNode?> SendAsync(HttpMethod method, string resourceId, string? action, string apiVersion, JsonNode? body, CancellationToken cancellationToken)
    {
        var token = _options.AccessToken is { } provider
            ? await provider(cancellationToken).ConfigureAwait(false)
            : throw new InvalidOperationException("Set AzureOptions.AccessToken, for example from Azure.Identity's DefaultAzureCredential.");

        var separator = action?.Contains('?', StringComparison.Ordinal) == true ? '&' : '?';
        var path = $"{resourceId.TrimEnd('/')}{action}{separator}api-version={Uri.EscapeDataString(apiVersion)}";
        var baseUri = _options.ResourceManager.AbsoluteUri.EndsWith('/') ? _options.ResourceManager : new Uri(_options.ResourceManager.AbsoluteUri + "/");
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        using var response = await _clients.CreateClient(HttpClientName).SendAsync(request, timeout.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ResourceManagerException(response.StatusCode, method, resourceId + action, Error(text));
        }

        try
        {
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks that <paramref name="resourceId"/> is an ARM resource id of the expected type, so a mistyped id cannot
    /// reach another endpoint: segments only, no query, no dot segments.
    /// </summary>
    internal static string ValidateResourceId(string resourceId, System.Text.RegularExpressions.Regex? type = null, string? typeName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        if (!ResourceIdPattern().IsMatch(resourceId) || resourceId.Split('/').Any(s => s is "." or ".."))
        {
            throw new ArgumentException($"'{resourceId}' is not an Azure resource id (/subscriptions/{{id}}/resourceGroups/{{group}}/providers/...).", nameof(resourceId));
        }

        if (type is not null && !type.IsMatch(resourceId))
        {
            throw new ArgumentException($"'{resourceId}' is not {typeName}.", nameof(resourceId));
        }

        return resourceId;
    }

    /// <summary>A path after the resource id, such as <c>/restart</c>: plain segments only.</summary>
    internal static bool IsValidAction(string action)
        => ActionPattern().IsMatch(action) && !action.Split('/').Any(s => s is "." or "..");

    [GeneratedRegex(@"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft\.Web/sites/[^/]+(/slots/[^/]+)?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    internal static partial Regex SitePattern();

    [GeneratedRegex(@"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft\.Compute/virtualMachines/[^/]+\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    internal static partial Regex VirtualMachinePattern();

    [GeneratedRegex(@"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft\.Cdn/profiles/[^/]+/originGroups/[^/]+/origins/[^/]+\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    internal static partial Regex FrontDoorOriginPattern();

    [GeneratedRegex(@"^(/[A-Za-z0-9\-_.~]+)+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ActionPattern();

    private static string Error(string text)
    {
        try
        {
            var error = JsonNode.Parse(text)?["error"];
            if (error?["message"]?.GetValue<string>() is { Length: > 0 } message)
            {
                return error["code"]?.GetValue<string>() is { Length: > 0 } code ? $"{code}: {message}" : message;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Not the usual error document: fall back to the raw text below.
        }

        return text.Length <= 200 ? text : text[..200];
    }

    [GeneratedRegex(@"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[-\w\._\(\)]+/providers/[A-Za-z0-9\.]+/[A-Za-z0-9\-_\.~/]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceIdPattern();
}

/// <summary>Azure Resource Manager answered with an error.</summary>
public sealed class ResourceManagerException : Exception
{
    internal ResourceManagerException(HttpStatusCode status, HttpMethod method, string target, string message)
        : base(status == HttpStatusCode.Forbidden
            ? $"Azure refused {method} {target}: {message}. Give the identity a role that allows this operation on the resource (see docs/cloud.md)."
            : $"Azure answered {(int)status} to {method} {target}: {message}")
        => StatusCode = status;

    /// <summary>HTTP status of the answer.</summary>
    public HttpStatusCode StatusCode { get; }
}
