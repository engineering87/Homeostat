using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Homeostat.Artemis;

/// <summary>Reads MBean attributes through Jolokia: one POST per read, so names need no path escaping.</summary>
internal sealed class JolokiaClient(IHttpClientFactory clients, ArtemisOptions options)
{
    internal const string HttpClientName = "Homeostat.Artemis";

    /// <summary>The Jolokia endpoint: the path of the management address, then the Jolokia path.</summary>
    internal static Uri Endpoint(Uri managementUrl, string jolokiaPath)
    {
        var builder = new UriBuilder(managementUrl)
        {
            Path = managementUrl.AbsolutePath.TrimEnd('/') + "/" + jolokiaPath.Trim('/') + "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    /// <summary>Scheme, host and port only.</summary>
    internal static string OriginOf(Uri url) => $"{url.Scheme}://{url.Authority}";

    public async Task<IReadOnlyDictionary<string, double>> ReadAsync(string mbean, IReadOnlyList<string> attributes, CancellationToken cancellationToken)
    {
        // A client from the factory for each read, so its handler is rotated (DNS changes are picked up).
        var http = clients.CreateClient(HttpClientName);
        var endpoint = Endpoint(options.ManagementUrl!, options.JolokiaPath);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { type = "read", mbean, attribute = attributes }), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Origin", options.Origin ?? OriginOf(options.ManagementUrl!));
        if (options.UserName is { } user)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{options.Password}")));
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(body, attributes);
    }

    /// <summary>
    /// Jolokia answers HTTP 200 even for errors, with the real status in the body; an unknown MBean (a missing queue)
    /// is a 404 there.
    /// </summary>
    internal static IReadOnlyDictionary<string, double> Parse(string body, IReadOnlyList<string> attributes)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        var status = root.TryGetProperty("status", out var s) && s.TryGetInt32(out var code) ? code : 0;
        if (status != 200)
        {
            var error = root.TryGetProperty("error_type", out var type) ? type.GetString() : "unknown error";
            throw new ArtemisManagementException(status == 404
                ? "The queue or address does not exist on the broker (Jolokia 404)."
                : $"The broker's management API answered status {status} ({error}).");
        }

        if (!root.TryGetProperty("value", out var value))
        {
            throw new ArtemisManagementException("The broker's management API answered without a value.");
        }

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            var element = attributes.Count == 1 && value.ValueKind == JsonValueKind.Number ? value
                : value.ValueKind == JsonValueKind.Object && value.TryGetProperty(attribute, out var v) ? v
                : default;
            if (element.ValueKind != JsonValueKind.Number)
            {
                throw new ArtemisManagementException($"The broker did not return the attribute {attribute}.");
            }

            values[attribute] = element.GetDouble();
        }

        return values;
    }

    /// <summary>The ObjectName of a queue, with the values quoted as JMX requires.</summary>
    internal static string QueueMBean(string broker, string address, string routingType, string queue)
        => $"org.apache.activemq.artemis:broker={Quote(broker)},component=addresses,address={Quote(address)}," +
           $"subcomponent=queues,routing-type={Quote(routingType)},queue={Quote(queue)}";

    internal static string Quote(string value)
    {
        var text = new StringBuilder("\"");
        foreach (var c in value)
        {
            text.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '*' => "\\*",
                '?' => "\\?",
                '\n' => "\\n",
                _ => c.ToString(),
            });
        }

        return text.Append('"').ToString();
    }
}

/// <summary>The Artemis management API refused a read.</summary>
public sealed class ArtemisManagementException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ArtemisManagementException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    public ArtemisManagementException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ArtemisManagementException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
