using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Homeostat.Tests;

public class DashboardTests
{
    [Fact]
    public async Task Dashboard_is_served_with_its_assets_and_a_strict_policy()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat();
        var app = builder.Build();
        app.MapHomeostatStatus(allowAnonymous: true);
        app.MapHomeostatDashboard(allowAnonymous: true);
        await app.StartAsync();
        await using var _ = app;
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.First()) };

        using var page = await client.GetAsync("/homeostat/dashboard");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        var csp = string.Join(";", page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("src=\"dashboard.js\"", html);   // relative: works under any prefix

        using var script = await client.GetAsync("/homeostat/dashboard.js");
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);
        var js = await script.Content.ReadAsStringAsync();
        Assert.False(js.Contains("innerHTML", StringComparison.Ordinal));   // server values are rendered as text only
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/homeostat/dashboard.css")).StatusCode);
    }

    [Fact]
    public async Task Control_requests_need_a_bearer_token_or_the_custom_header()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat();
        var app = builder.Build();
        app.MapHomeostatControl(allowAnonymous: true);
        app.MapHomeostatDashboard(allowAnonymous: true);
        await app.StartAsync();
        await using var _ = app;
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.First()) };
        var control = app.Services.GetRequiredService<IHomeostatControl>();

        async Task<HttpStatusCode> PauseAsync(params (string Name, string Value)[] headers)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/homeostat/pause");
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await client.SendAsync(request);
            control.Resume();
            return response.StatusCode;
        }

        // A form posted by another site: ambient credentials (cookies, Windows authentication), no custom header.
        Assert.Equal(HttpStatusCode.Forbidden, await PauseAsync());
        Assert.Equal(HttpStatusCode.Forbidden, await PauseAsync(("Cookie", "session=abc")));
        Assert.Equal(HttpStatusCode.Forbidden, await PauseAsync(("X-Requested-With", "x"), ("Sec-Fetch-Site", "cross-site")));

        Assert.Equal(HttpStatusCode.NoContent, await PauseAsync(("X-Requested-With", "homeostat-dashboard"), ("Sec-Fetch-Site", "same-origin")));
        Assert.Equal(HttpStatusCode.NoContent, await PauseAsync(("Authorization", "Bearer token")));

        // The dashboard's relative URLs only work without a trailing slash.
        using var slash = await client.GetAsync("/homeostat/dashboard/");
        Assert.Equal(HttpStatusCode.Redirect, slash.StatusCode);
        Assert.Equal("/homeostat/dashboard", slash.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Pause_duration_is_capped()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat();
        var app = builder.Build();
        app.MapHomeostatControl(allowAnonymous: true);
        await app.StartAsync();
        await using var _ = app;
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.First()) };
        client.DefaultRequestHeaders.Add("X-Requested-With", "tests");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/homeostat/pause?for=10675199.02:48:05", null)).StatusCode);
    }

    [Fact]
    public void Dashboard_requires_a_policy_or_an_explicit_opt_out()
    {
        var builder = WebApplication.CreateSlimBuilder();
        var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.MapHomeostatDashboard());
        app.MapHomeostatDashboard("operations", prefix: "/ops/homeostat");

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/ops/homeostat/dashboard");
        Assert.Equal("operations", endpoint.Metadata.GetMetadata<IAuthorizeData>()?.Policy);
    }
}
