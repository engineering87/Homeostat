using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Homeostat.Kubernetes;

/// <summary>The few Kubernetes API calls Homeostat needs, over plain HTTPS.</summary>
internal sealed class KubernetesApi
{
    internal const string HttpClientName = "Homeostat.Kubernetes";
    private readonly IHttpClientFactory _clients;
    private readonly KubernetesOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IServiceProvider _services;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private (string Token, DateTimeOffset ReadAt)? _token;

    public KubernetesApi(IHttpClientFactory clients, IOptions<KubernetesOptions> options, TimeProvider timeProvider, IServiceProvider services)
    {
        _clients = clients;
        _options = options.Value;
        _timeProvider = timeProvider;
        _services = services;
    }

    public KubernetesOptions Options => _options;

    public string Namespace => KubernetesNames.Validate(Required(_options.Namespace, "namespace", "Namespace (or the service account namespace file)"), "namespace");

    public string PodName => KubernetesNames.Validate(Required(_options.PodName, "pod name", "PodName (or the POD_NAME variable, from the downward API)"), "pod name");

    public string NodeName => KubernetesNames.Validate(Required(_options.NodeName, "node name", "NodeName (or the NODE_NAME variable, from the downward API)"), "node name");

    public Task<JsonNode?> PostAsync(string path, JsonNode body, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Post, path, body, "application/json", cancellationToken);

    public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, path, null, null, cancellationToken);

    public Task<JsonNode?> PatchAsync(string path, JsonNode body, string contentType, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Patch, path, body, contentType, cancellationToken);

    /// <summary>
    /// Builds the handler: TLS validated against the cluster CA when the file exists, the system store otherwise.
    /// Validation is never turned off.
    /// </summary>
    internal static HttpMessageHandler CreateHandler(KubernetesOptions options)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (KubernetesOptions.Read(options.CertificateAuthorityPath) is { Length: > 0 } pem)
        {
            var authorities = new X509Certificate2Collection();
            authorities.ImportFromPem(pem);
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors)
                => IsTrusted(certificate as X509Certificate2, chain, errors, authorities);
        }

        return handler;
    }

    internal static bool IsTrusted(X509Certificate2? certificate, X509Chain? presented, SslPolicyErrors errors, X509Certificate2Collection authorities)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        // Only an untrusted chain can be fixed by the cluster CA; a missing certificate or a wrong name cannot.
        if (certificate is null || errors != SslPolicyErrors.RemoteCertificateChainErrors)
        {
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(authorities);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;

        // A server certificate, not a client certificate signed by the same cluster CA.
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));

        // Intermediates sent by the server, when ca.crt holds only the root.
        if (presented is not null)
        {
            foreach (var element in presented.ChainElements)
            {
                if (!element.Certificate.Equals(certificate))
                {
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }
        }

        return chain.Build(certificate);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, string? contentType, CancellationToken cancellationToken)
    {
        try
        {
            return await SendOnceAsync(method, path, body, contentType, forceTokenRead: false, cancellationToken).ConfigureAwait(false);
        }
        catch (KubernetesApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized && _options.AccessToken is null)
        {
            // The token may have rotated within the cache minute: read it again once.
            return await SendOnceAsync(method, path, body, contentType, forceTokenRead: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<JsonNode?> SendOnceAsync(HttpMethod method, string path, JsonNode? body, string? contentType, bool forceTokenRead, CancellationToken cancellationToken)
    {
        var server = _options.ApiServer
            ?? throw new InvalidOperationException(
                "The Kubernetes API server is unknown: run inside a pod (KUBERNETES_SERVICE_HOST) or set KubernetesOptions.ApiServer.");

        // Relative to the server address, keeping its path (for example a proxy at /k8s/clusters/c-1/).
        var baseUri = server.AbsoluteUri.EndsWith('/') ? server : new Uri(server.AbsoluteUri + "/");
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path.TrimStart('/')));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(forceTokenRead, cancellationToken).ConfigureAwait(false));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType!);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        using var response = await _clients.CreateClient(HttpClientName).SendAsync(request, timeout.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new KubernetesApiException(response.StatusCode, method, path, StatusMessage(text));
        }

        try
        {
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"The Kubernetes API server answered {method} {path} with something that is not JSON: is ApiServer right?");
        }
    }

    private async ValueTask<string> GetTokenAsync(bool forceRead, CancellationToken cancellationToken)
    {
        if (_options.AccessToken is { } provider)
        {
            return await provider(_services, cancellationToken).ConfigureAwait(false);
        }

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (forceRead || _token is not { } cached || now - cached.ReadAt > TimeSpan.FromMinutes(1))
            {
                var token = KubernetesOptions.Read(_options.TokenPath)
                    ?? throw new InvalidOperationException(
                        $"The service account token {_options.TokenPath} cannot be read: set automountServiceAccountToken, or KubernetesOptions.AccessToken.");
                _token = (token, now);
            }

            return _token!.Value.Token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    /// <summary>The message of a Kubernetes <c>Status</c> body, or a short excerpt.</summary>
    private static string StatusMessage(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject status && status["message"] is JsonValue value
                && value.TryGetValue<string>(out var message) && message.Length > 0)
            {
                return message;
            }
        }
        catch (JsonException)
        {
            // Not a Status object: fall back to the raw text below.
        }

        return text.Length <= 200 ? text : text[..200];
    }

    private static string Required(string? value, string what, string setting)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"The {what} is unknown: set KubernetesOptions.{setting}.")
            : value;
}

/// <summary>Kubernetes object names: DNS subdomains, so a name cannot change the request target.</summary>
internal static partial class KubernetesNames
{
    public static string Validate(string name, string what)
        => name.Length <= 253 && Pattern().IsMatch(name)
            ? name
            : throw new ArgumentException($"'{name}' is not a valid Kubernetes {what} (lowercase letters, digits, '-' and '.', starting and ending with a letter or digit).");

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z0-9]([-a-z0-9.]*[a-z0-9])?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}

/// <summary>The Kubernetes API answered with an error.</summary>
public sealed class KubernetesApiException : Exception
{
    internal KubernetesApiException(HttpStatusCode status, HttpMethod method, string path, string message)
        : base(Describe(status, method, path, message))
        => StatusCode = status;

    /// <summary>HTTP status of the answer.</summary>
    public HttpStatusCode StatusCode { get; }

    private static string Describe(HttpStatusCode status, HttpMethod method, string path, string message) => status switch
    {
        HttpStatusCode.Forbidden =>
            $"Kubernetes refused {method} {path}: {message}. Grant the service account the permission with a Role and RoleBinding (see docs/cloud.md).",
        HttpStatusCode.Unauthorized => $"Kubernetes did not accept the service account token for {method} {path}: {message}",
        _ => $"Kubernetes answered {(int)status} to {method} {path}: {message}",
    };
}
