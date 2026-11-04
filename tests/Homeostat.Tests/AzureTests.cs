using System.Net;
using System.Text.Json;
using Homeostat.Azure;
using Homeostat.Healing;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Tests;

public class AzureTests
{
    private const string Subscription = "/subscriptions/00000000-1111-2222-3333-444444444444/resourceGroups/weather-rg";
    private const string Site = Subscription + "/providers/Microsoft.Web/sites/weather-api";
    private const string Vm = Subscription + "/providers/Microsoft.Compute/virtualMachines/radar-01";
    private const string Origin = Subscription + "/providers/Microsoft.Cdn/profiles/weather-fd/originGroups/api/origins/westeurope";

    private static Task<HomeostatTestHost> StartAsync(FakeArm arm, Action<HealingPlanBuilder> plan, bool heals = false)
        => HomeostatTestHost.StartAsync(
            h => h
                .UseAzure(o => o.AccessToken = (_, _) => ValueTask.FromResult("arm-token"))
                .AddCondition("api-down", c => c.ForSource("api").Heal(p =>
                {
                    plan(p);
                    if (heals)
                    {
                        p.VerifyWith<BuiltInActionTests.AlwaysHealed>();
                    }
                    else
                    {
                        p.VerifyWith<CachingTests.NeverHealed>();
                    }
                })),
            s => s.AddHttpClient("Homeostat.Azure").ConfigurePrimaryHttpMessageHandler(() => arm));

    [Fact]
    public async Task Actions_call_resource_manager_with_the_token()
    {
        var arm = new FakeArm();
        await using var host = await StartAsync(arm, p => p
            .RestartAppService(Site, softRestart: true, requireApproval: false)
            .RestartVirtualMachine(Vm, requireApproval: false)
            .AzureResourceManager("Restart revision", HttpMethod.Post, Subscription + "/providers/Microsoft.App/containerApps/forecast/revisions/forecast--v7", "/restart", "2024-03-01", requireApproval: false));

        host.Publish("api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("api-down");

        Assert.All(run.Steps, s => Assert.True(s.Succeeded, s.Error?.ToString()));
        Assert.All(arm.Calls, c => Assert.Equal("Bearer arm-token", c.Authorization));
        Assert.Equal(
            [
                $"POST https://management.azure.com{Site}/restart?softRestart=true&synchronous=false&api-version=2024-04-01",
                $"POST https://management.azure.com{Vm}/restart?api-version=2024-07-01",
                $"POST https://management.azure.com{Subscription}/providers/Microsoft.App/containerApps/forecast/revisions/forecast--v7/restart?api-version=2024-03-01",
            ],
            arm.Calls.Select(c => $"{c.Method} {c.Uri}"));
    }

    private const string OtherOrigin = Subscription + "/providers/Microsoft.Cdn/profiles/weather-fd/originGroups/api/origins/northeurope";

    private static FakeArm FrontDoor(string originState, string otherState) => new()
    {
        Respond = (method, uri) => method != "GET" ? string.Empty
            : uri.Contains("/origins?", StringComparison.Ordinal)
                ? $$$"""{"value":[{"id":"{{{Origin}}}","properties":{"enabledState":"{{{originState}}}"}},{"id":"{{{OtherOrigin}}}","properties":{"enabledState":"{{{otherState}}}"}}]}"""
                : $$$"""{"id":"{{{Origin}}}","properties":{"enabledState":"{{{originState}}}"}}""",
    };

    [Fact]
    public async Task Front_Door_origin_is_disabled_and_enabled_again_when_it_did_not_help()
    {
        var arm = FrontDoor("Enabled", "Enabled");
        await using var host = await StartAsync(arm, p => p.DisableFrontDoorOrigin(Origin, requireApproval: false));

        host.Publish("api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("api-down");

        Assert.True(run.Steps.Single().UndoSucceeded);
        var patches = arm.Calls.Where(c => c.Method == "PATCH").ToList();
        Assert.Equal(["Disabled", "Enabled"], patches.Select(c => c.Body!.Value.GetProperty("properties").GetProperty("enabledState").GetString()!));
        Assert.All(patches, c => Assert.Equal($"https://management.azure.com{Origin}?api-version=2024-02-01", c.Uri));
        Assert.Contains(arm.Calls, c => c.Uri == $"https://management.azure.com{Subscription}/providers/Microsoft.Cdn/profiles/weather-fd/originGroups/api/origins?api-version=2024-02-01");
    }

    [Theory]
    [InlineData("Enabled", "Disabled", "only enabled origin")]
    [InlineData("Disabled", "Enabled", "already disabled")]
    public async Task Front_Door_failover_never_stops_all_traffic_nor_undoes_someone_else(string origin, string other, string expected)
    {
        var arm = FrontDoor(origin, other);
        await using var host = await StartAsync(arm, p => p.DisableFrontDoorOrigin(Origin, requireApproval: false));

        host.Publish("api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("api-down");

        Assert.Contains(expected, run.Steps.Single().Error!.Message);
        Assert.Null(run.Steps.Single().UndoSucceeded);           // nothing changed, nothing to undo
        Assert.DoesNotContain(arm.Calls, c => c.Method == "PATCH");
    }

    [Fact]
    public async Task Azure_actions_require_approval_by_default()
    {
        var arm = new FakeArm();
        await using var host = await StartAsync(arm, p => p.RestartAppService(Site));

        host.Publish("api", SignalSeverity.Unhealthy);
        await PollyControlTests.WaitUntilAsync(() => host.Approvals.GetPending().Count == 1);

        Assert.Empty(arm.Calls);
    }

    [Fact]
    public async Task Errors_carry_the_resource_manager_message()
    {
        var arm = new FakeArm { Status = HttpStatusCode.Forbidden };
        await using var host = await StartAsync(arm, p => p.RestartAppService(Site, requireApproval: false));

        host.Publish("api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("api-down");

        var error = Assert.IsType<ResourceManagerException>(run.Steps.Single().Error);
        Assert.Contains("AuthorizationFailed", error.Message);
        Assert.Contains("role", error.Message);
    }

    [Theory]
    [InlineData("weather-api")]
    [InlineData("/subscriptions/x/resourceGroups/rg/providers/Microsoft.Web/sites/a")]
    [InlineData(Subscription + "/providers/Microsoft.Web/sites/../../../providers/Microsoft.Compute/virtualMachines/b")]
    [InlineData(Subscription + "/providers/Microsoft.Web/sites/a?x=1")]
    [InlineData(Vm)]
    [InlineData(Site + "\n")]
    [InlineData(Subscription + "/providers/Microsoft.Web/serverfarms/weather-plan")]
    [InlineData(Site + "/config/web")]
    public void Resource_ids_are_validated(string id)
    {
        new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddHomeostat(h => h.AddCondition("x", c => c.ForSource("x").Heal(p =>
        {
            Assert.Throws<ArgumentException>(() => p.RestartAppService(id));
            p.With("ok", _ => { });
        })));
    }

    [Fact]
    public void App_Service_slots_are_accepted_and_generic_actions_are_validated()
    {
        new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddHomeostat(h => h.AddCondition("x", c => c.ForSource("x").Heal(p =>
        {
            p.RestartAppService(Site + "/slots/staging");
            foreach (var action in new[] { "restart", "/restart#x", "/a/../b", "/./restart", "/%2e%2e/x", "/restart?x=1", "//restart" })
            {
                Assert.Throws<ArgumentException>(() => p.AzureResourceManager("x", HttpMethod.Post, Site, action, "2024-04-01"));
            }

            p.AzureResourceManager("ok", HttpMethod.Post, Site, "/restart", "2024-04-01");
        })));
    }

    [Fact]
    public async Task Azure_settings_are_validated_at_startup()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => HomeostatTestHost.StartAsync(h => h.UseAzure(_ => { })));
        await Assert.ThrowsAnyAsync<Exception>(() => HomeostatTestHost.StartAsync(h => h.UseAzure(o =>
        {
            o.AccessToken = (_, _) => ValueTask.FromResult("t");
            o.ResourceManager = new Uri("http://management.azure.com/");
        })));
    }

    internal sealed record Call(string Method, string Uri, string? Authorization, JsonElement? Body);

    internal sealed class FakeArm : HttpMessageHandler
    {
        private readonly List<Call> _calls = [];

        public HttpStatusCode Status { get; init; } = HttpStatusCode.Accepted;

        public Func<string, string, string>? Respond { get; init; }

        public IReadOnlyList<Call> Calls
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
            var body = request.Content is null ? null : (JsonElement?)JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            lock (_calls)
            {
                _calls.Add(new Call(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
            }

            var content = Status == HttpStatusCode.Forbidden
                ? """{"error":{"code":"AuthorizationFailed","message":"The client does not have authorization to perform action 'Microsoft.Web/sites/restart/action'."}}"""
                : Respond?.Invoke(request.Method.Method, request.RequestUri!.AbsoluteUri) ?? string.Empty;
            return new HttpResponseMessage(Status == HttpStatusCode.Accepted && request.Method == HttpMethod.Get ? HttpStatusCode.OK : Status)
            {
                Content = new StringContent(content),
            };
        }
    }
}
