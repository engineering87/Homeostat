using Grpc.Core;
using Grpc.Core.Interceptors;
using Homeostat.Calls;

namespace Homeostat.Grpc;

/// <summary>
/// Client interceptor that records the status of every call in a <see cref="CallRecorder"/>. A unary call is recorded
/// with its duration when its response arrives. Streaming calls are recorded without a duration, since a stream lasts as
/// long as the caller keeps it open and would distort the latency percentile: a client streaming call when its response
/// arrives, a server or duplex streaming call when its response stream ends or fails (with the final status from the
/// trailers) or when the call cannot start (the server is unreachable). Calls canceled by the caller are not recorded,
/// and neither is a streaming call abandoned before its end. Added by <c>AddHomeostatGrpcSignals</c>; create it
/// yourself for a channel built without the client factory (<c>channel.Intercept(interceptor)</c>).
/// </summary>
public sealed class HomeostatGrpcInterceptor : Interceptor
{
    private readonly CallRecorder _recorder;
    private readonly Func<StatusCode, bool> _isFailure;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the interceptor.</summary>
    /// <param name="recorder">Where calls are recorded, usually the keyed <see cref="CallRecorder"/> of the client's source.</param>
    /// <param name="isFailure">Which statuses count as failures; <see cref="GrpcSignalOptions.IsServiceFailure"/> when <c>null</c>.</param>
    /// <param name="timeProvider">Clock for durations; the system clock when <c>null</c>.</param>
    public HomeostatGrpcInterceptor(CallRecorder recorder, Func<StatusCode, bool>? isFailure = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        _recorder = recorder;
        _isFailure = isFailure ?? GrpcSignalOptions.IsServiceFailure;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var call = Start(timed: true);
        try
        {
            var response = continuation(request, context);
            call.Complete(StatusCode.OK);
            return response;
        }
        catch (Exception ex)
        {
            call.Fail(ex);
            throw;
        }
    }

    /// <inheritdoc />
    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var call = Start(timed: true);
        var inner = continuation(request, context);
        Observe(inner.ResponseAsync, call);
        return inner;
    }

    /// <inheritdoc />
    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context, AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var call = Start(timed: false);
        var inner = continuation(context);
        Observe(inner.ResponseAsync, call);
        return inner;
    }

    /// <inheritdoc />
    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context, AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var call = Start(timed: false);
        var inner = continuation(request, context);
        var headers = inner.ResponseHeadersAsync;
        ObserveFault(headers, call);
        return new AsyncServerStreamingCall<TResponse>(
            new ObservedReader<TResponse>(inner.ResponseStream, call, inner.GetStatus), headers, inner.GetStatus, inner.GetTrailers, inner.Dispose);
    }

    /// <inheritdoc />
    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context, AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var call = Start(timed: false);
        var inner = continuation(context);
        var headers = inner.ResponseHeadersAsync;
        ObserveFault(headers, call);
        return new AsyncDuplexStreamingCall<TRequest, TResponse>(
            inner.RequestStream, new ObservedReader<TResponse>(inner.ResponseStream, call, inner.GetStatus), headers,
            inner.GetStatus, inner.GetTrailers, inner.Dispose);
    }

    private CallOutcome Start(bool timed) => new(this, timed ? _timeProvider.GetTimestamp() : null);

    // The caller keeps the original task (and sees its exception unchanged); the continuation only reads the outcome.
    private static void Observe<TResponse>(Task<TResponse> response, CallOutcome call)
        => response.ContinueWith(
            static (task, state) =>
            {
                var outcome = (CallOutcome)state!;
                if (task.IsCompletedSuccessfully)
                {
                    outcome.Complete(StatusCode.OK);
                }
                else
                {
                    outcome.Fail(task.Exception?.InnerException);
                }
            },
            call, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // The headers of a streaming call fail only when the call cannot start: record it even if nobody reads the stream.
    private static void ObserveFault(Task<Metadata> headers, CallOutcome call)
        => headers.ContinueWith(
            static (task, state) => ((CallOutcome)state!).Fail(task.Exception?.InnerException),
            call, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>One call: recorded once, by whichever end comes first.</summary>
    private sealed class CallOutcome(HomeostatGrpcInterceptor owner, long? started)
    {
        private int _recorded;

        public void Complete(StatusCode status)
        {
            // A call canceled by the caller says nothing about the service.
            if (status == StatusCode.Cancelled || Interlocked.Exchange(ref _recorded, 1) != 0)
            {
                return;
            }

            // A classifier that throws must not reach the application's call: it counts as not failed.
            bool failed;
            try
            {
                failed = owner._isFailure(status);
            }
            catch
            {
                failed = false;
            }

            if (started is { } timestamp)
            {
                owner._recorder.Record(failed, owner._timeProvider.GetElapsedTime(timestamp));
            }
            else
            {
                owner._recorder.Record(failed);
            }
        }

        // Grpc.Net.Client reports every failure as an RpcException (a task canceled before the call started as an
        // OperationCanceledException); anything else (a broken interceptor further down, for example) counts as a failure.
        public void Fail(Exception? exception) => Complete(exception switch
        {
            RpcException rpc => rpc.StatusCode,
            OperationCanceledException => StatusCode.Cancelled,
            _ => StatusCode.Unknown,
        });
    }

    /// <summary>A response stream that records the call when the stream ends or fails.</summary>
    private sealed class ObservedReader<T>(IAsyncStreamReader<T> inner, CallOutcome call, Func<Status> status) : IAsyncStreamReader<T>
    {
        public T Current => inner.Current;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            bool more;
            try
            {
                more = await inner.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                call.Fail(ex);
                throw;
            }

            if (!more)
            {
                // The stream ended: the final status (from the trailers) is available now. Reading it must not turn
                // the caller's normal end of stream into an exception.
                StatusCode final;
                try
                {
                    final = status().StatusCode;
                }
                catch (InvalidOperationException)
                {
                    final = StatusCode.Unknown;
                }

                call.Complete(final);
            }

            return more;
        }
    }
}
