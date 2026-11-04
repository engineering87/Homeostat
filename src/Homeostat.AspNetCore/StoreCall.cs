namespace Homeostat.AspNetCore;

/// <summary>Store calls made while answering a request.</summary>
internal static class StoreCall
{
    /// <summary>
    /// Waits at most <paramref name="timeout"/>, even for a store that blocks before returning its task (synchronous I/O,
    /// a lock): the call starts on the thread pool, so the request thread never waits longer than that.
    /// </summary>
    public static Task<T> Run<T>(Func<ValueTask<T>> call, TimeSpan timeout, CancellationToken cancellationToken)
        => Task.Run(() => call().AsTask(), CancellationToken.None).WaitAsync(timeout, cancellationToken);

    /// <inheritdoc cref="Run{T}(Func{ValueTask{T}}, TimeSpan, CancellationToken)"/>
    public static Task Run(Func<ValueTask> call, TimeSpan timeout, CancellationToken cancellationToken)
        => Task.Run(() => call().AsTask(), CancellationToken.None).WaitAsync(timeout, cancellationToken);
}
