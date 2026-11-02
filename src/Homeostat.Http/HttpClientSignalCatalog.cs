namespace Homeostat.Http;

/// <summary>One <c>AddHomeostatSignals()</c> call on a named client.</summary>
internal sealed record HttpClientSignalRegistration(string ClientName, HttpClientSignalOptions Options);

/// <summary>Holds the collection window of every instrumented client.</summary>
internal sealed class HttpClientSignalCatalog
{
    private readonly Dictionary<string, HttpClientWindow> _windows;

    public HttpClientSignalCatalog(IEnumerable<HttpClientSignalRegistration> registrations)
    {
        _windows = new Dictionary<string, HttpClientWindow>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            // The last registration for a client wins, like most configuration in .NET.
            _windows[registration.ClientName] = new HttpClientWindow(registration.ClientName, registration.Options);
        }
    }

    public IEnumerable<HttpClientWindow> Windows => _windows.Values;

    public HttpClientWindow Get(string clientName)
        => _windows.TryGetValue(clientName, out var window)
            ? window
            : throw new InvalidOperationException($"HTTP client '{clientName}' is not instrumented by Homeostat.");
}
