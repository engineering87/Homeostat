using System.Net;
using System.Reflection;
using System.Text.Json;
using Homeostat.Coordination;
using Homeostat.OpenFeature;
using Homeostat.Persistence;
using Homeostat.RabbitMQ;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using RabbitMQ.Client;
using StackExchange.Redis;

namespace Homeostat.Tests;

public class FinalReviewIntegrationTests
{
    // ---------------------------------------------------------------- RabbitMQ: a cancel that timed out

    [Fact]
    public async Task A_RabbitMQ_cancel_that_timed_out_is_sent_again_on_resume_and_the_consumer_subscribes_again()
    {
        // The first cancel times out on an open channel (the broker under a resource alarm); it may have been applied
        // all the same, the late cancel-ok being discarded by the client.
        var tags = 0;
        var cancels = 0;
        MessagingTests.Recorder<IChannel>? recorderRef = null;
        var (channel, recorder) = MessagingTests.Recorder<IChannel>.Create((method, args) => method.Name switch
        {
            "BasicConsumeAsync" => Task.FromResult($"tag-{++tags}"),
            "BasicCancelAsync" => ++cancels == 1
                ? Task.FromException(new TimeoutException("cancel-ok not received in time"))
                : ConfirmCancel(recorderRef!, (string)args[0]!),
            "get_IsOpen" => true,
            _ => throw new NotSupportedException(method.Name),
        });
        recorderRef = recorder;
        var consumer = new PausableRabbitMqConsumer("thumbnails", channel, "thumbnails", autoAck: false, new AsyncDefaultBasicConsumerStub(channel));

        await consumer.StartAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => consumer.PauseAsync(CancellationToken.None));
        Assert.True(consumer.IsConsuming);   // unknown: the broker may still have the subscription

        // The condition clears: the resume cancels the old tag again (the broker answers cancel-ok, known or not),
        // then subscribes, so the queue has a consumer again.
        await consumer.ResumeAsync(CancellationToken.None);
        Assert.Equal(["BasicConsumeAsync", "BasicCancelAsync", "BasicCancelAsync", "BasicConsumeAsync"], recorder.Calls.Select(c => c.Method).Where(m => m != "get_IsOpen"));
        Assert.Equal(["tag-1", "tag-1"], recorder.Calls.Where(c => c.Method == "BasicCancelAsync").Select(c => (string)c.Arguments[0]!));
        Assert.True(consumer.IsConsuming);

        // A cancel-ok is matched by value and ends only the subscription it names.
        var wrapper = (IAsyncBasicConsumer)recorder.Calls[^1].Arguments[6]!;
        await wrapper.HandleBasicCancelOkAsync("tag-1");
        Assert.True(consumer.IsConsuming);
        await wrapper.HandleBasicCancelOkAsync(new string("tag-2".AsSpan()));
        Assert.False(consumer.IsConsuming);
    }

    [Fact]
    public async Task A_RabbitMQ_resume_whose_second_cancel_fails_stays_paused_and_is_retried()
    {
        var cancels = 0;
        var (channel, recorder) = MessagingTests.Recorder<IChannel>.Create((method, _) => method.Name switch
        {
            "BasicConsumeAsync" => Task.FromResult("tag-1"),
            "BasicCancelAsync" => ++cancels <= 2 ? Task.FromException(new TimeoutException("cancel-ok not received in time")) : Task.CompletedTask,
            "get_IsOpen" => true,
            _ => throw new NotSupportedException(method.Name),
        });
        var consumer = new PausableRabbitMqConsumer("thumbnails", channel, "thumbnails", autoAck: false, new AsyncDefaultBasicConsumerStub(channel));

        await consumer.StartAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => consumer.PauseAsync(CancellationToken.None));

        // Fail closed: no second subscription next to one that may still be alive.
        await Assert.ThrowsAsync<TimeoutException>(() => consumer.ResumeAsync(CancellationToken.None));
        Assert.Equal(1, recorder.Calls.Count(c => c.Method == "BasicConsumeAsync"));

        await consumer.ResumeAsync(CancellationToken.None);
        Assert.Equal(["BasicConsumeAsync", "BasicCancelAsync", "BasicCancelAsync", "BasicCancelAsync", "BasicConsumeAsync"], recorder.Calls.Select(c => c.Method).Where(m => m != "get_IsOpen"));
        Assert.True(consumer.IsConsuming);
    }

    /// <summary>What the client does when cancel-ok arrives in time: tells the consumer, then completes.</summary>
    private static async Task ConfirmCancel(MessagingTests.Recorder<IChannel> recorder, string tag)
    {
        var consumer = recorder.Calls.Last(c => c.Method == "BasicConsumeAsync" && Equals(c.Arguments[0], "thumbnails")).Arguments[6] as IAsyncBasicConsumer;
        await consumer!.HandleBasicCancelOkAsync(tag);
    }

    private sealed class AsyncDefaultBasicConsumerStub(IChannel channel) : AsyncDefaultBasicConsumer(channel);

    // ---------------------------------------------------------------- Azure Front Door: a failure after the change

    private const string Subscription = "/subscriptions/00000000-1111-2222-3333-444444444444/resourceGroups/weather-rg";
    private const string Origin = Subscription + "/providers/Microsoft.Cdn/profiles/weather-fd/originGroups/api/origins/westeurope";
    private const string OtherOrigin = Subscription + "/providers/Microsoft.Cdn/profiles/weather-fd/originGroups/api/origins/northeurope";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_Front_Door_origin_is_enabled_again_when_the_step_fails_after_disabling_it(bool patchFails)
    {
        // Either the disable itself fails (ARM may have applied it before the 503) or the re-read of the group is
        // throttled: the step throws, so no undo will run, and the origin must not stay out of rotation.
        var arm = new ScriptedArm(patchFails);

        await using var host = await HomeostatTestHost.StartAsync(
            h => h
                .UseAzure(o => o.AccessToken = (_, _) => ValueTask.FromResult("arm-token"))
                .AddCondition("api-down", c => c.ForSource("api").Heal(p => p
                    .DisableFrontDoorOrigin(Origin, requireApproval: false)
                    .UseVerifier<CachingTests.NeverHealed>())),
            s => s.AddHttpClient("Homeostat.Azure").ConfigurePrimaryHttpMessageHandler(() => arm));

        host.Publish("api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("api-down");

        var step = Assert.Single(run.Steps);
        Assert.False(step.Succeeded);
        Assert.Contains(patchFails ? "503" : "429", step.Error!.Message);   // the original error is passed on
        var patches = arm.Calls.Where(c => c.Method == "PATCH").Select(c => c.State).ToList();
        Assert.Equal(["Disabled", "Enabled"], patches);
    }

    private sealed class ScriptedArm(bool patchFails) : HttpMessageHandler
    {
        private readonly List<(string Method, string? State)> _calls = [];
        private int _groupReads;
        private int _patches;

        public IReadOnlyList<(string Method, string? State)> Calls
        {
            get
            {
                lock (_calls)
                {
                    return _calls.ToArray();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? state = null;
            if (request.Content is not null)
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                state = body.RootElement.GetProperty("properties").GetProperty("enabledState").GetString();
            }

            lock (_calls)
            {
                _calls.Add((request.Method.Method, state));
            }

            var uri = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Patch)
            {
                return Interlocked.Increment(ref _patches) == 1 && patchFails
                    ? Answer(HttpStatusCode.ServiceUnavailable, """{"error":{"code":"ServiceUnavailable","message":"Try again later."}}""")
                    : Answer(HttpStatusCode.OK, "{}");
            }

            if (!uri.Contains("/origins?", StringComparison.Ordinal))
            {
                return Answer(HttpStatusCode.OK, $$$"""{"id":"{{{Origin}}}","properties":{"enabledState":"Enabled"}}""");
            }

            return Interlocked.Increment(ref _groupReads) == 1
                ? Answer(HttpStatusCode.OK, $$$"""{"value":[{"id":"{{{Origin}}}","properties":{"enabledState":"Enabled"}},{"id":"{{{OtherOrigin}}}","properties":{"enabledState":"Enabled"}}]}""")
                : Answer(HttpStatusCode.TooManyRequests, """{"error":{"code":"TooManyRequests","message":"Rate limit exceeded."}}""");
        }

        private static HttpResponseMessage Answer(HttpStatusCode status, string content) => new(status) { Content = new StringContent(content) };
    }

    // ---------------------------------------------------------------- Redis: the store keeps its own client

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UseRedis_with_a_connection_string_keeps_the_control_state_off_the_application_client(bool applicationClientFirst)
    {
        var connection = RedisFixture.Require();
        using var real = ConnectionMultiplexer.Connect(connection);
        var application = CountingConnection.Wrap(real);

        var services = new ServiceCollection().AddLogging();
        if (applicationClientFirst)
        {
            services.AddSingleton(application);
        }

        services.AddHomeostat(h => h.UseRedis(connection, o => o.KeyPrefix = RedisFixture.Prefix()));
        if (!applicationClientFirst)
        {
            services.AddSingleton(application);
        }

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IHomeostatStore>();
        Assert.True(await store.SetPauseAsync(new PauseState { At = DateTimeOffset.UtcNow, By = "on-call" }));
        Assert.Equal("on-call", (await store.GetControlStateAsync()).Pause?.By);
        var lease = await provider.GetRequiredService<IHomeostatCoordinator>().TryAcquireAsync("plan:feed-down", TimeSpan.FromSeconds(5));
        Assert.NotNull(lease);
        await lease!.DisposeAsync();

        // The application's client is still the registered one (AddRedisSignals pings it), but carried nothing of Homeostat's.
        Assert.Same(application, provider.GetRequiredService<IConnectionMultiplexer>());
        Assert.Equal(0, ((CountingConnection)(object)application).Uses);
    }

    [Fact]
    public async Task UseRedis_with_a_connection_string_registers_its_client_when_the_application_has_none()
    {
        var connection = RedisFixture.Require();
        var services = new ServiceCollection().AddLogging();
        services.AddHomeostat(h => h.UseRedis(connection, o => o.KeyPrefix = RedisFixture.Prefix()));
        await using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<IHomeostatStore>();
        Assert.Null((await store.GetControlStateAsync()).Pause);
        var registered = provider.GetRequiredService<IConnectionMultiplexer>();
        Assert.True(registered.IsConnected);
    }

    /// <summary>A connection that counts the calls made through it.</summary>
    public class CountingConnection : DispatchProxy
    {
        private object _target = null!;
        private int _uses;

        public int Uses => Volatile.Read(ref _uses);

        public static IConnectionMultiplexer Wrap(IConnectionMultiplexer connection)
        {
            var proxy = Create<IConnectionMultiplexer, CountingConnection>();
            ((CountingConnection)(object)proxy)._target = connection;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name is not nameof(IDisposable.Dispose) and not nameof(IAsyncDisposable.DisposeAsync))
            {
                Interlocked.Increment(ref _uses);
            }

            return targetMethod.Invoke(_target, args);
        }
    }

    // ---------------------------------------------------------------- OpenFeature: change events are not lost

    [Fact]
    public async Task OpenFeature_change_events_announce_every_switch_turned_on_back_to_back()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddHomeostat();
        await using var provider = services.BuildServiceProvider();
        var switches = provider.GetRequiredService<IHomeostatSwitches>();
        var domain = $"homeostat-{Guid.NewGuid():N}";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Api.Instance.SetProviderAsync(domain, new HomeostatSwitchProvider(switches));
        var client = Api.Instance.GetClient(domain);
        client.AddHandler(ProviderEventTypes.ProviderConfigurationChanged, payload =>
        {
            lock (seen)
            {
                seen.UnionWith(payload?.FlagsChanged ?? []);
                if (seen.Contains("shed-forecast-maps") && seen.Contains("serve-cached-forecast"))
                {
                    both.TrySetResult();
                }
            }
        });

        // Back to back: the SDK's event channel holds one event, so the second change arrives while the first is unread.
        switches.TurnOn("shed-forecast-maps");
        switches.TurnOn("serve-cached-forecast");

        await both.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task OpenFeature_provider_initialized_again_after_a_shutdown_announces_changes()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddHomeostat();
        await using var provider = services.BuildServiceProvider();
        var switches = provider.GetRequiredService<IHomeostatSwitches>();
        var homeostat = new HomeostatSwitchProvider(switches);

        await homeostat.InitializeAsync(EvaluationContext.Empty);
        switches.TurnOn("shed-forecast-maps");
        switches.TurnOn("serve-cached-forecast");   // the pump waits: nothing reads the channel yet
        await homeostat.ShutdownAsync();
        await homeostat.InitializeAsync(EvaluationContext.Empty);

        var reader = homeostat.GetEventChannel().Reader;
        var first = (ProviderEventPayload)await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["shed-forecast-maps"], first.FlagsChanged!);

        // A change after the restart is announced, even if it arrived while the stopped pump was still winding down.
        switches.TurnOn("reports-read-only");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var announced = new List<string>();
        while (!announced.Contains("reports-read-only") && DateTime.UtcNow < deadline)
        {
            var next = (ProviderEventPayload)await reader.ReadAsync().AsTask().WaitAsync(deadline - DateTime.UtcNow);
            announced.AddRange(next.FlagsChanged ?? []);
        }

        Assert.Contains("reports-read-only", announced);
        await homeostat.ShutdownAsync();
    }
}
