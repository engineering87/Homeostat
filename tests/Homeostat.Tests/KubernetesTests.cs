using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Homeostat.Healing;
using Homeostat.Kubernetes;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Homeostat.Tests;

public sealed class KubernetesTests : IAsyncDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("homeostat-k8s-").FullName;
    private readonly List<(string Method, string Path, string? ContentType, string? Authorization, string Body)> _requests = [];
    private WebApplication? _server;

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A fake API server over HTTPS, with a certificate signed by a CA generated for the test.</summary>
    private async Task<(Uri Server, string CaPath)> StartApiServerAsync(
        HttpStatusCode status = HttpStatusCode.OK, int replicas = 2, bool podHasController = true, bool nodeCordoned = false, string serverName = "localhost")
    {
        var (ca, server) = Certificates(serverName);
        var caPath = Path.Combine(_directory, "ca.crt");
        await File.WriteAllTextAsync(caPath, ca.ExportCertificatePem());
        await File.WriteAllTextAsync(Path.Combine(_directory, "token"), "sa-token-1\n");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(server)));
        var app = builder.Build();
        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            lock (_requests)
            {
                _requests.Add((context.Request.Method, context.Request.Path.ToUriComponent(), context.Request.ContentType,
                    context.Request.Headers.Authorization.ToString(), body));
            }

            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            var path = context.Request.Path.Value!;
            var get = context.Request.Method == "GET";
            await context.Response.WriteAsync(status != HttpStatusCode.OK
                ? """{"kind":"Status","message":"deployments.apps \"weather-api\" is forbidden","reason":"Forbidden","code":403}"""
                : get && path.EndsWith("/scale", StringComparison.Ordinal) ? $$$"""{"metadata":{"resourceVersion":"42"},"spec":{"replicas":{{{replicas}}}}}"""
                : get && path.Contains("/pods/", StringComparison.Ordinal) ? (podHasController
                    ? """{"metadata":{"name":"weather-api-7d9","ownerReferences":[{"kind":"ReplicaSet","name":"weather-api-5f","controller":true}]}}"""
                    : """{"metadata":{"name":"weather-api-7d9"}}""")
                : get && path.StartsWith("/api/v1/nodes/", StringComparison.Ordinal) ? $$$"""{"spec":{"unschedulable":{{{(nodeCordoned ? "true" : "false")}}}}}"""
                : "{}");
        });
        await app.StartAsync();
        _server = app;
        var port = new Uri(app.Urls.First()).Port;
        return (new Uri($"https://localhost:{port}"), caPath);
    }

    private Task<HomeostatTestHost> StartHostAsync(Uri server, string caPath, Action<HealingPlanBuilder> plan)
        => HomeostatTestHost.StartAsync(h => h
            .UseKubernetes(o =>
            {
                o.ApiServer = server;
                o.CertificateAuthorityPath = caPath;
                o.TokenPath = Path.Combine(_directory, "token");
                o.Namespace = "weather";
                o.PodName = "weather-api-7d9";
                o.NodeName = "node-1";
            })
            .AddCondition("weather-api-sick", c => c.ForSource("weather-api").Heal(p => plan(p.VerifyWith<CachingTests.NeverHealed>()))));

    [Fact]
    public async Task Actions_call_the_api_server_with_the_service_account_token()
    {
        var (server, caPath) = await StartApiServerAsync();
        await using var host = await StartHostAsync(server, caPath, p => p
            .RolloutRestart("weather-api", requireApproval: false)
            .ScaleDeployment("weather-api", by: 2, maximum: 3, requireApproval: false)
            .CordonNode(requireApproval: false)
            .RestartPod(gracePeriodSeconds: 15, requireApproval: false, limits: new RestartLimits(TimeSpan.Zero, 3, TimeSpan.FromHours(1))));

        host.Publish("weather-api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("weather-api-sick");

        Assert.All(run.Steps, s => Assert.True(s.Succeeded, s.Error?.ToString()));
        var requests = _requests.ToArray();
        Assert.All(requests, r => Assert.Equal("Bearer sa-token-1", r.Authorization));

        Assert.Equal(("PATCH", "/apis/apps/v1/namespaces/weather/deployments/weather-api", "application/strategic-merge-patch+json"),
            (requests[0].Method, requests[0].Path, requests[0].ContentType));
        Assert.True(JsonDocument.Parse(requests[0].Body).RootElement.GetProperty("spec").GetProperty("template")
            .GetProperty("metadata").GetProperty("annotations").TryGetProperty("kubectl.kubernetes.io/restartedAt", out _));

        Assert.Equal(("GET", "/apis/apps/v1/namespaces/weather/deployments/weather-api/scale"), (requests[1].Method, requests[1].Path));
        Assert.Equal(("PATCH", "application/merge-patch+json"), (requests[2].Method, requests[2].ContentType));
        var scale = JsonDocument.Parse(requests[2].Body).RootElement;
        Assert.Equal(3, scale.GetProperty("spec").GetProperty("replicas").GetInt32());                  // 2 + 2, capped at 3
        Assert.Equal("42", scale.GetProperty("metadata").GetProperty("resourceVersion").GetString());   // conditional update

        Assert.Equal(("GET", "/api/v1/nodes/node-1"), (requests[3].Method, requests[3].Path));
        Assert.Equal(("PATCH", "/api/v1/nodes/node-1"), (requests[4].Method, requests[4].Path));
        Assert.True(JsonDocument.Parse(requests[4].Body).RootElement.GetProperty("spec").GetProperty("unschedulable").GetBoolean());

        Assert.Equal(("GET", "/api/v1/namespaces/weather/pods/weather-api-7d9"), (requests[5].Method, requests[5].Path));
        Assert.Equal(("DELETE", "/api/v1/namespaces/weather/pods/weather-api-7d9"), (requests[6].Method, requests[6].Path));
        Assert.Equal(15, JsonDocument.Parse(requests[6].Body).RootElement.GetProperty("gracePeriodSeconds").GetInt32());
    }

    [Fact]
    public async Task Kubernetes_actions_require_approval_by_default()
    {
        var (server, caPath) = await StartApiServerAsync();
        await using var host = await StartHostAsync(server, caPath, p => p.RestartPod());

        host.Publish("weather-api", SignalSeverity.Unhealthy);
        await PollyControlTests.WaitUntilAsync(() => host.Approvals.GetPending().Count == 1);

        Assert.Empty(_requests);
    }

    [Fact]
    public async Task An_api_server_signed_by_another_authority_is_not_trusted()
    {
        var (server, _) = await StartApiServerAsync();
        var (otherCa, _) = Certificates();
        var otherPath = Path.Combine(_directory, "other-ca.crt");
        await File.WriteAllTextAsync(otherPath, otherCa.ExportCertificatePem());
        await using var host = await StartHostAsync(server, otherPath, p => p.RolloutRestart("weather-api", requireApproval: false));

        host.Publish("weather-api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("weather-api-sick");

        Assert.False(run.Steps.Single().Succeeded);
        Assert.IsType<HttpRequestException>(run.Steps.Single().Error);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Forbidden_answers_explain_the_missing_permission()
    {
        var (server, caPath) = await StartApiServerAsync(HttpStatusCode.Forbidden);
        await using var host = await StartHostAsync(server, caPath, p => p.RolloutRestart("weather-api", requireApproval: false));

        host.Publish("weather-api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("weather-api-sick");

        var error = Assert.IsType<KubernetesApiException>(run.Steps.Single().Error);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("is forbidden", error.Message);
        Assert.Contains("Role", error.Message);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../../nodes/node-1")]
    [InlineData("Weather-API")]
    [InlineData("weather-api\n")]
    public void Names_that_could_change_the_request_target_are_refused(string name)
    {
        new ServiceCollection().AddHomeostat(h => h.AddCondition("x", c => c.ForSource("x").Heal(p =>
        {
            Assert.Throws<ArgumentException>(() => p.RolloutRestart(name));
            Assert.Throws<ArgumentException>(() => p.ScaleDeployment(name, by: 1, maximum: 3));
            p.With("ok", _ => { });
        })));
    }

    [Fact]
    public async Task Scaling_never_goes_the_other_way()
    {
        var (server, caPath) = await StartApiServerAsync(replicas: 12);
        await using var host = await StartHostAsync(server, caPath, p => p.ScaleDeployment("weather-api", by: 2, maximum: 10, requireApproval: false));

        host.Publish("weather-api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("weather-api-sick");

        Assert.Contains("cannot scale it out", run.Steps.Single().Error!.Message);
        Assert.Equal("GET", _requests.Single().Method);        // 12 replicas stay 12
    }

    [Fact]
    public async Task A_pod_without_a_controller_and_an_already_cordoned_node_are_left_alone()
    {
        var (server, caPath) = await StartApiServerAsync(podHasController: false, nodeCordoned: true);
        await using var host = await StartHostAsync(server, caPath, p => p
            .CordonNode(requireApproval: false)
            .RestartPod(requireApproval: false, limits: new RestartLimits(TimeSpan.Zero, 3, TimeSpan.FromHours(1))));

        host.Publish("weather-api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("weather-api-sick");

        Assert.Contains("already cordoned", run.Steps[0].Error!.Message);
        Assert.Contains("no controller", run.Steps[1].Error!.Message);
        Assert.All(_requests, r => Assert.Equal("GET", r.Method));
    }

    [Fact]
    public async Task A_server_certificate_for_another_name_is_not_trusted_even_from_the_cluster_authority()
    {
        var (server, caPath) = await StartApiServerAsync(serverName: "kubernetes.other.example");
        await using var host = await StartHostAsync(server, caPath, p => p.RolloutRestart("weather-api", requireApproval: false));

        host.Publish("weather-api", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("weather-api-sick");

        Assert.IsType<HttpRequestException>(run.Steps.Single().Error);
        Assert.Empty(_requests);
    }

    /// <summary>A CA and a server certificate for localhost signed by it.</summary>
    private static (X509Certificate2 Ca, X509Certificate2 Server) Certificates(string serverName = "localhost")
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=homeostat-test-ca", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        var now = DateTimeOffset.UtcNow;
        using var ca = caRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest($"CN={serverName}", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(serverName);
        if (serverName == "localhost")
        {
            names.AddIpAddress(IPAddress.Loopback);
        }

        serverRequest.CertificateExtensions.Add(names.Build());
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var signed = serverRequest.Create(ca, now.AddDays(-1), now.AddDays(29), [1, 2, 3, 4, 5, 6, 7, 8]);
        using var withKey = signed.CopyWithPrivateKey(serverKey);

        return (X509CertificateLoader.LoadCertificate(ca.Export(X509ContentType.Cert)),
            X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null));
    }
}
