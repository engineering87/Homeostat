namespace Homeostat.Http;

/// <summary>Handlers shared by the clients that deliver notifications.</summary>
internal static class HomeostatHttpHandlers
{
    /// <summary>
    /// A redirect is not a delivery: followed, it would turn the POST into a GET without a body (answered 2xx, so the
    /// event would count as delivered) and send custom secret headers to another host. It fails like any non-2xx
    /// answer. Applied to the primary handler, whichever the application configured.
    /// </summary>
    public static void WithoutRedirects(HttpMessageHandler handler, IServiceProvider services)
    {
        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.AllowAutoRedirect = false;
                break;
            case HttpClientHandler client:
                client.AllowAutoRedirect = false;
                break;
        }
    }
}
