using Homeostat.Cli;
using Homeostat.Signals;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Homeostat.Tests;

public class CliTests
{
    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat(h => h
            .Configure(o => { o.DryRun = false; o.ApplicationName = "weather-api"; })
            .AddCondition("feed-down", c => c.ForSource("alert:feed").Heal(p => p
                .With("failover", _ => { }).RequireApproval(TimeSpan.FromMinutes(5))
                .VerifyWith<BuiltInActionTests.AlwaysHealed>())));
        var app = builder.Build();
        app.MapHomeostatStatus(allowAnonymous: true);
        app.MapHomeostatControl(allowAnonymous: true);
        app.MapHomeostatAlerts(allowAnonymous: true);
        await app.StartAsync();
        return app;
    }

    private static async Task<(int Code, string Output, string Error)> RunAsync(WebApplication app, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await HomeostatCli.InvokeAsync([.. args, "--url", app.Urls.First()], output, error,
            new HttpClientHandler { UseProxy = false });
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Cli_reads_status_sends_signals_and_answers_approvals()
    {
        await using var app = await StartAsync();

        var (code, output, _) = await RunAsync(app, "status");
        Assert.Equal(0, code);
        Assert.Contains("weather-api on ", output);
        Assert.Contains("feed-down", output);

        (code, output, _) = await RunAsync(app, "signal", "feed", "Unhealthy", "-m", "provider timeout");
        Assert.Equal(0, code);
        Assert.Contains("accepted", output);

        var approvals = app.Services.GetRequiredService<IHomeostatApprovals>();
        await PollyControlTests.WaitUntilAsync(() => approvals.GetPending().Count == 1);
        var id = approvals.GetPending()[0].Id;

        (code, output, _) = await RunAsync(app, "status", "--fail-on-active");
        Assert.Equal(HomeostatCli.ActiveConditions, code);
        Assert.Contains(id, output);
        Assert.Contains("homeostat approve <id>", output);

        (code, _, _) = await RunAsync(app, "approve", id);
        Assert.Equal(0, code);
        Assert.Empty(approvals.GetPending());

        (code, _, var error) = await RunAsync(app, "approve", id);
        Assert.Equal(HomeostatCli.Failed, code);
        Assert.Contains("Not found", error);
    }

    [Fact]
    public async Task Cli_pauses_resumes_and_flips_switches()
    {
        await using var app = await StartAsync();
        var control = app.Services.GetRequiredService<IHomeostatControl>();
        var switches = app.Services.GetRequiredService<IHomeostatSwitches>();

        Assert.Equal(0, (await RunAsync(app, "pause", "--for", "30m")).Code);
        Assert.True(control.IsPaused);
        Assert.Equal(0, (await RunAsync(app, "resume")).Code);
        Assert.False(control.IsPaused);

        Assert.Equal(0, (await RunAsync(app, "switch", "on", "circuit:forecast:isolated")).Code);
        Assert.True(switches.IsOn("circuit:forecast:isolated"));
        Assert.Equal(0, (await RunAsync(app, "switch", "off", "circuit:forecast:isolated")).Code);
        Assert.False(switches.IsOn("circuit:forecast:isolated"));

        var (code, _, error) = await RunAsync(app, "pause", "--for", "soon");
        Assert.Equal(HomeostatCli.Failed, code);
        Assert.Contains("not a positive duration", error);
    }

    [Fact]
    public async Task Cli_explains_missing_address_and_wrong_prefix()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(HomeostatCli.Failed, await HomeostatCli.InvokeAsync(["status", "--url", "not a url"], output, error));
        Assert.Contains("--url", error.ToString());

        await using var app = await StartAsync();
        var (code, _, message) = await RunAsync(app, "status", "--prefix", "/ops");
        Assert.Equal(HomeostatCli.Failed, code);
        Assert.Contains("prefix", message);
    }

    [Fact]
    public async Task Cli_keeps_the_path_of_the_url_and_never_leaves_the_server()
    {
        var handler = new RecordingHandler();
        var output = new StringWriter();
        var error = new StringWriter();

        await HomeostatCli.InvokeAsync(["resume", "--url", "https://gateway.internal/weather-api", "--token", "t0k"], output, error, handler);
        Assert.Equal("https://gateway.internal/weather-api/homeostat/resume", handler.Requests.Single().AbsoluteUri);

        Assert.Equal(HomeostatCli.Failed, await HomeostatCli.InvokeAsync(["resume", "--url", "https://gateway.internal", "--prefix", "https://evil.example"], output, error, handler));
        Assert.Equal(HomeostatCli.Failed, await HomeostatCli.InvokeAsync(["pause", "--for", "99999999d", "--url", "https://gateway.internal"], output, error, handler));
        Assert.Single(handler.Requests);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("homeostat-cli", request.Headers.GetValues("X-Requested-With").Single());
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        }
    }

    [Theory]
    [InlineData("30m", "00:30:00")]
    [InlineData("2h", "02:00:00")]
    [InlineData("1d", "1.00:00:00")]
    [InlineData("00:45:00", "00:45:00")]
    public void Durations_are_converted_for_the_pause_endpoint(string input, string expected)
        => Assert.Equal(expected, Durations.ToHms(input));
}
