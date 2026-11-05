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

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var start = _timeProvider.GetTimestamp();
        Task<HttpResponseMessage> sending;
        try
        {
            sending = base.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            // Rethrown from the async path below, so the caller sees exactly what an async handler would give it.
            sending = Task.FromException<HttpResponseMessage>(ex);
        }

        // Already answered (a cached or stubbed response): no state machine, nothing allocated.
        if (sending.IsCompletedSuccessfully)
        {
            Completed(sending.Result, start);
            return sending;
        }

        return AwaitAsync(sending, start, cancellationToken);
    }

    private void Completed(HttpResponseMessage response, long start)
    {
        // Measuring must never fail the application's call: a classifier that throws counts the response as a
        // success, and the caller gets it as if Homeostat were not there.
        bool failed;
        try
        {
            failed = _window.Options.IsFailure(response);
        }
        catch
        {
            failed = false;
        }

        _window.Record(failed, _timeProvider.GetElapsedTime(start));
    }

    private async Task<HttpResponseMessage> AwaitAsync(Task<HttpResponseMessage> sending, long start, CancellationToken cancellationToken)
    {
        try
        {
            var response = await sending.ConfigureAwait(false);
            Completed(response, start);
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
