namespace Homeostat.Http;

/// <summary>Records the outcome and latency of every request sent by an instrumented client.</summary>
internal sealed class HomeostatSignalHandler : DelegatingHandler
{
    private readonly HttpClientWindow _window;
    private readonly TimeProvider _timeProvider;

    public HomeostatSignalHandler(HttpClientWindow window, TimeProvider timeProvider)
    {
        _window = window;
        _timeProvider = timeProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var start = _timeProvider.GetTimestamp();
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            bool failed;
            try
            {
                failed = _window.Options.IsFailure(response);
            }
            catch
            {
                // The caller never receives the response, so release it here.
                response.Dispose();
                throw;
            }

            _window.Record(failed, _timeProvider.GetElapsedTime(start));
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Either an HttpClient timeout or a cancellation by the caller: indistinguishable here.
            if (_window.Options.CountCancellationsAsFailures)
            {
                _window.Record(failed: true, _timeProvider.GetElapsedTime(start));
            }

            throw;
        }
        catch
        {
            _window.Record(failed: true, _timeProvider.GetElapsedTime(start));
            throw;
        }
    }
}
