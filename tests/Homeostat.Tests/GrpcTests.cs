using System.Net;
using System.Text;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Homeostat.Calls;
using Homeostat.Detectors;
using Homeostat.Grpc;
using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Homeostat.Tests;

public class GrpcTests
{
    private static readonly Marshaller<string> Text = Marshallers.Create(s => Encoding.UTF8.GetBytes(s), b => Encoding.UTF8.GetString(b));
    private static readonly Method<string, string> Unary = new(MethodType.Unary, "forecasts.Forecasts", "Get", Text, Text);
    private static readonly Method<string, string> Stream = new(MethodType.ServerStreaming, "forecasts.Forecasts", "Watch", Text, Text);
    private static readonly Method<string, string> Duplex = new(MethodType.DuplexStreaming, "forecasts.Forecasts", "Exchange", Text, Text);

    [Fact]
    public async Task Unary_calls_are_judged_on_their_status()
    {
        var recorder = new CallRecorder("grpc:forecasts", new CallSignalOptions { MinimumCalls = 1 });
        var invoker = new FakeInvoker().Intercept(new HomeostatGrpcInterceptor(recorder));

        Assert.Equal("ok", await invoker.AsyncUnaryCall(Unary, null, default, "ok"));
        await Assert.ThrowsAsync<RpcException>(async () => await invoker.AsyncUnaryCall(Unary, null, default, "unavailable"));
        await Assert.ThrowsAsync<RpcException>(async () => await invoker.AsyncUnaryCall(Unary, null, default, "not-found"));
        Assert.Throws<RpcException>(() => invoker.BlockingUnaryCall(Unary, null, default, "internal"));

        var signal = recorder.Collect(DateTimeOffset.UtcNow, "grpc-client")!;
        Assert.Equal("4", signal.Tags!["calls"]);
        Assert.Equal("2", signal.Tags["failures"]);     // Unavailable and Internal; NotFound is the caller's problem
    }

    [Fact]
    public async Task Streaming_calls_are_recorded_once_with_the_final_status_from_the_trailers()
    {
        var recorder = new CallRecorder("grpc:forecasts", new CallSignalOptions { MinimumCalls = 1 });
        var invoker = new FakeInvoker().Intercept(new HomeostatGrpcInterceptor(recorder));

        // Two messages, then the stream ends with Internal in the trailers: no exception, but a failure.
        using var failing = invoker.AsyncServerStreamingCall(Stream, null, default, "internal-at-end");
        var received = new List<string>();
        while (await failing.ResponseStream.MoveNext(CancellationToken.None))
        {
            received.Add(failing.ResponseStream.Current);
        }

        Assert.False(await failing.ResponseStream.MoveNext(CancellationToken.None));   // reading again records nothing more
        using var healthy = invoker.AsyncServerStreamingCall(Stream, null, default, "ok");
        while (await healthy.ResponseStream.MoveNext(CancellationToken.None))
        {
        }

        Assert.Equal(["a", "b"], received);
        var signal = recorder.Collect(DateTimeOffset.UtcNow, "grpc-client")!;
        Assert.Equal("2", signal.Tags!["calls"]);
        Assert.Equal("1", signal.Tags["failures"]);
        Assert.Equal("n/a", signal.Tags["p95_ms"]);     // a stream lasts as long as the caller wants: no duration
    }

    [Fact]
    public async Task Canceled_calls_are_not_recorded_and_a_duplex_call_that_cannot_start_is_recorded_unread()
    {
        var recorder = new CallRecorder("grpc:forecasts", new CallSignalOptions { MinimumCalls = 1 });
        var invoker = new FakeInvoker().Intercept(new HomeostatGrpcInterceptor(recorder));

        await Assert.ThrowsAsync<RpcException>(async () => await invoker.AsyncUnaryCall(Unary, null, default, "cancelled"));
        Assert.Null(recorder.Collect(DateTimeOffset.UtcNow, "grpc-client"));

        // The server is unreachable: the headers fail, and the call counts although nobody reads its stream.
        using var exchange = invoker.AsyncDuplexStreamingCall(Duplex, null, default);
        var signal = recorder.Collect(DateTimeOffset.UtcNow, "grpc-client")!;
        Assert.Equal("1", signal.Tags!["calls"]);
        Assert.Equal("1", signal.Tags["failures"]);

        // Reading it afterwards does not record it again.
        await Assert.ThrowsAsync<RpcException>(() => exchange.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Null(recorder.Collect(DateTimeOffset.UtcNow, "grpc-client"));
    }

    [Fact]
    public async Task The_caller_sees_the_original_response_task_and_its_exception()
    {
        var recorder = new CallRecorder("grpc:forecasts", new CallSignalOptions { MinimumCalls = 1 });
        var invoker = new FakeInvoker().Intercept(new HomeostatGrpcInterceptor(recorder));

        var call = invoker.AsyncUnaryCall(Unary, null, default, "unavailable");
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
        Assert.Equal(StatusCode.Unavailable, error.StatusCode);
        Assert.Same(FakeInvoker.LastResponse, call.ResponseAsync);
    }

    [Fact]
    public async Task Adding_the_signals_twice_to_a_client_does_not_count_its_calls_twice()
    {
        var services = new ServiceCollection();
        services.AddHomeostat();
        services.AddGrpcClient<ForecastsClient>(o => o.Address = new Uri("https://forecasts.invalid"))
            .ConfigurePrimaryHttpMessageHandler(() => new TrailersOnlyHandler(StatusCode.Unavailable))
            .AddHomeostatGrpcSignals()
            .AddHomeostatGrpcSignals(o => o.MinimumCalls = 1);
        var provider = services.BuildServiceProvider();

        await Assert.ThrowsAsync<RpcException>(async () => await provider.GetRequiredService<ForecastsClient>().GetAsync("rome"));

        var sink = new CollectingPublisher();
        await provider.GetServices<ISignalDetector>().OfType<CallSignalDetector>().Single().DetectAsync(sink, CancellationToken.None);
        var signal = Assert.Single(sink.Signals);
        Assert.Equal("1", signal.Tags!["calls"]);
        Assert.Equal(SignalSeverity.Unhealthy, signal.Severity);
    }

    [Fact]
    public async Task A_client_factory_client_publishes_grpc_signals_end_to_end()
    {
        var time = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddHomeostat();
        services.AddGrpcClient<ForecastsClient>(o => o.Address = new Uri("https://forecasts.invalid"))
            .ConfigurePrimaryHttpMessageHandler(() => new TrailersOnlyHandler(StatusCode.Unavailable))
            .AddHomeostatGrpcSignals(o => o.MinimumCalls = 2);
        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<ForecastsClient>();

        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<RpcException>(async () => await client.GetAsync("rome"));
            Assert.Equal(StatusCode.Unavailable, error.StatusCode);
        }

        var sink = new CollectingPublisher();
        await provider.GetServices<ISignalDetector>().OfType<CallSignalDetector>().Single().DetectAsync(sink, CancellationToken.None);
        var signal = Assert.Single(sink.Signals);
        Assert.Equal("grpc:ForecastsClient", signal.Source);
        Assert.Equal(SignalSeverity.Unhealthy, signal.Severity);
        Assert.Equal("grpc-client", signal.Tags!["origin"]);
    }

    /// <summary>A generated-style client with one unary method, written by hand (no code generation in the tests).</summary>
    public sealed class ForecastsClient(CallInvoker invoker) : ClientBase<ForecastsClient>(invoker)
    {
        public AsyncUnaryCall<string> GetAsync(string city) => CallInvoker.AsyncUnaryCall(Unary, null, default, city);

        protected override ForecastsClient NewInstance(ClientBaseConfiguration configuration) => throw new NotSupportedException();
    }

    /// <summary>Answers every call as a gRPC server that fails before sending a message: HTTP 200 with the status in the headers.</summary>
    private sealed class TrailersOnlyHandler(StatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20, RequestMessage = request, Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/grpc");
            response.Headers.Add("grpc-status", ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
            response.Headers.Add("grpc-message", "down");
            return Task.FromResult(response);
        }
    }

    /// <summary>Answers by request: "ok", or the status named in the request; streams send "a", "b" then end.</summary>
    private sealed class FakeInvoker : CallInvoker
    {
        public static Task? LastResponse { get; private set; }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => AsyncUnaryCall(method, host, options, request).ResponseAsync.GetAwaiter().GetResult();

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var text = (string)(object)request!;
            var response = text switch
            {
                "ok" => Task.FromResult((TResponse)(object)"ok"),
                "unavailable" => Task.FromException<TResponse>(new RpcException(new Status(StatusCode.Unavailable, "down"))),
                "not-found" => Task.FromException<TResponse>(new RpcException(new Status(StatusCode.NotFound, "no such city"))),
                "cancelled" => Task.FromException<TResponse>(new RpcException(new Status(StatusCode.Cancelled, "canceled by the caller"))),
                _ => Task.FromException<TResponse>(new RpcException(new Status(StatusCode.Internal, "bug"))),
            };
            LastResponse = response;
            return new AsyncUnaryCall<TResponse>(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var status = (string)(object)request! == "ok" ? Status.DefaultSuccess : new Status(StatusCode.Internal, "failed after streaming");
            var reader = new ListReader<TResponse>([(TResponse)(object)"a", (TResponse)(object)"b"]);
            return new AsyncServerStreamingCall<TResponse>(reader, Task.FromResult(new Metadata()), () => status, () => new Metadata(), () => { });
        }

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();

        /// <summary>A server that cannot be reached: the headers and the response stream fail with Unavailable.</summary>
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
        {
            var unavailable = new RpcException(new Status(StatusCode.Unavailable, "connection refused"));
            return new AsyncDuplexStreamingCall<TRequest, TResponse>(
                new FailingWriter<TRequest>(unavailable), new FailingReader<TResponse>(unavailable), Task.FromException<Metadata>(unavailable),
                () => unavailable.Status, () => new Metadata(), () => { });
        }
    }

    private sealed class ListReader<T>(IReadOnlyList<T> items) : IAsyncStreamReader<T>
    {
        private int _index = -1;

        public T Current => items[_index];

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_index + 1 < items.Count)
            {
                _index++;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }

    private sealed class FailingReader<T>(Exception error) : IAsyncStreamReader<T>
    {
        public T Current => throw new InvalidOperationException();

        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromException<bool>(error);
    }

    private sealed class FailingWriter<T>(Exception error) : IClientStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get; set; }

        public Task CompleteAsync() => Task.FromException(error);

        public Task WriteAsync(T message) => Task.FromException(error);
    }
}
