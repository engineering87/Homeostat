using System.Net;
using System.Security.Cryptography;
using System.Text;
using Homeostat.Signals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Homeostat.Tests;

public class SlackInteractionTests
{
    private const string Secret = "8f742231b10e8888abcd99yyyzzz85a5";

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat(h => h
            .Configure(o => o.DryRun = false)
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                .With("failover", _ => { }).RequireApproval(TimeSpan.FromMinutes(5))
                .UseVerifier<BuiltInActionTests.AlwaysHealed>())));
        var app = builder.Build();
        app.MapHomeostatSlackInteractions(o =>
        {
            o.SigningSecret = Secret;
            o.AllowedUserIds.Add("U-OPS");
        });
        await app.StartAsync();
        return (app, new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static HttpRequestMessage Request(string payloadJson, DateTimeOffset? at = null, string? secret = null)
    {
        var body = "payload=" + Uri.EscapeDataString(payloadJson);
        var timestamp = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signature = "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret ?? Secret), Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}")));
        var request = new HttpRequestMessage(HttpMethod.Post, "/homeostat/slack/interactions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        request.Headers.Add("X-Slack-Signature", signature);
        return request;
    }

    private static string Click(string action, string approvalId, string userId)
        => $$"""
            { "type": "block_actions", "user": { "id": "{{userId}}", "username": "ana" },
              "actions": [ { "action_id": "{{action}}", "value": "{{approvalId}}" } ],
              "response_url": "https://evil.example/steal" }
            """;

    private static async Task<string> WaitForApprovalAsync(WebApplication app)
    {
        var approvals = app.Services.GetRequiredService<IHomeostatApprovals>();
        app.Services.GetRequiredService<ISignalPublisher>().TryPublish("feed", SignalSeverity.Unhealthy);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (approvals.GetPending().Count == 0)
        {
            await Task.Delay(10, cts.Token);
        }

        return approvals.GetPending()[0].Id;
    }

    [Fact]
    public async Task Allowed_user_approves_with_a_signed_request()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        var id = await WaitForApprovalAsync(app);

        using var response = await client.SendAsync(Request(Click("homeostat_approve", id, "U-OPS")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(app.Services.GetRequiredService<IHomeostatApprovals>().GetPending());
    }

    [Fact]
    public async Task Unsigned_old_or_unauthorized_requests_do_not_approve()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;
        var id = await WaitForApprovalAsync(app);
        var approvals = app.Services.GetRequiredService<IHomeostatApprovals>();

        using (var wrongSecret = await client.SendAsync(Request(Click("homeostat_approve", id, "U-OPS"), secret: "another-secret")))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        }

        using (var replayed = await client.SendAsync(Request(Click("homeostat_approve", id, "U-OPS"), at: DateTimeOffset.UtcNow.AddMinutes(-10))))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        }

        using (var stranger = await client.SendAsync(Request(Click("homeostat_approve", id, "U-STRANGER"))))
        {
            Assert.Equal(HttpStatusCode.OK, stranger.StatusCode);   // Slack needs a 200; the user is told privately.
        }

        Assert.Single(approvals.GetPending());

        using (var reject = await client.SendAsync(Request(Click("homeostat_reject", id, "U-OPS"))))
        {
            Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        }

        Assert.Empty(approvals.GetPending());
    }

    [Fact]
    public void Slack_endpoint_requires_a_secret_and_allowed_users()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddHomeostat();
        var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.MapHomeostatSlackInteractions(_ => { }));
        Assert.Throws<ArgumentException>(() => app.MapHomeostatSlackInteractions(o => o.SigningSecret = Secret));
        app.MapHomeostatSlackInteractions(o => { o.SigningSecret = Secret; o.AllowAnyChannelMember = true; });
    }
}
