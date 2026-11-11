using System.Collections.Immutable;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Aspire.Hosting;
using Homeostat.Analyzers;
using Homeostat.Cli;
using Homeostat.Persistence;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Homeostat.Tests;

/// <summary>Findings of the final review of the hosting and tooling packages.</summary>
public class FinalReviewHostingTests
{
    // ---------------------------------------------------------------- status with a non-finite signal value

    [Fact]
    public async Task A_pending_approval_whose_signal_value_is_NaN_is_shown_by_the_status_history_and_cli()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddHomeostat(h => h
            .Configure(o => { o.DryRun = false; o.ApplicationName = "weather-api"; })
            .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                .With("failover", _ => { }).RequireApproval(TimeSpan.FromMinutes(5))
                .UseVerifier<BuiltInActionTests.AlwaysHealed>())));
        await using var app = builder.Build();
        app.MapHomeostatStatus(allowAnonymous: true);
        app.MapHomeostatControl(allowAnonymous: true);
        await app.StartAsync();

        var approvals = app.Services.GetRequiredService<IHomeostatApprovals>();
        app.Services.GetRequiredService<ISignalPublisher>().TryPublish("feed", SignalSeverity.Unhealthy, "no data", double.NaN);
        await PollyControlTests.WaitUntilAsync(() => approvals.GetPending().Count == 1);

        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(app.Urls.First()) };
        using var status = await client.GetAsync("/homeostat/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var approval = Assert.Single(JsonDocument.Parse(await status.Content.ReadAsStringAsync()).RootElement.GetProperty("pendingApprovals").EnumerateArray());
        Assert.Equal("NaN", approval.GetProperty("triggeringSignal").GetProperty("value").GetString());

        using var history = await client.GetAsync("/homeostat/history");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);

        var output = new StringWriter();
        var error = new StringWriter();
        var code = await HomeostatCli.InvokeAsync(["status", "--url", app.Urls.First()], output, error, new HttpClientHandler { UseProxy = false });
        Assert.True(code == HomeostatCli.Ok, error.ToString());
        Assert.Contains(approvals.GetPending()[0].Id, output.ToString());
    }

    // ---------------------------------------------------------------- CLI: the problem's detail is shown

    [Theory]
    [InlineData("The Homeostat store did not answer in time; the change may still be applied. Check GET /status before trying again.")]
    [InlineData("The Homeostat store is unavailable; nothing was changed (GET /status shows the error).")]
    public async Task The_cli_shows_the_detail_of_a_problem_answer_and_fails(string detail)
    {
        var body = JsonSerializer.Serialize(new { type = "https://tools.ietf.org/html/rfc9110#section-15.6.4", title = "Service Unavailable", status = 503, detail });
        using var handler = new FixedHandler(HttpStatusCode.ServiceUnavailable, body, "application/problem+json");
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await HomeostatCli.InvokeAsync(["approve", "abc", "--url", "https://weather-api.internal"], output, error, handler);

        Assert.Equal(HomeostatCli.Failed, code);
        Assert.Contains("503", error.ToString());
        Assert.Contains(detail, error.ToString());
    }

    [Fact]
    public void The_cli_reads_problem_details_strings_and_ignores_other_bodies()
    {
        Assert.Equal("was not made", Client.Detail("""{"title":"Service Unavailable","detail":"was not made"}"""));
        Assert.Equal("Service Unavailable", Client.Detail("""{"title":"Service Unavailable"}"""));
        Assert.Equal("'for' must be positive.", Client.Detail("\"'for' must be positive.\""));
        Assert.Equal("a b", Client.Detail("\"a\\rb\""));   // control characters never reach the terminal
        Assert.Null(Client.Detail(string.Empty));
        Assert.Null(Client.Detail("<html>Bad gateway</html>"));
        Assert.Equal(503, Client.Detail(JsonSerializer.Serialize(new string('x', 2000)))!.Length);
    }

    [Fact]
    public async Task The_dashboard_shows_the_detail_of_a_failed_change()
    {
        using var reader = new StreamReader(typeof(HomeostatDashboardExtensions).Assembly.GetManifestResourceStream("Homeostat.AspNetCore.Dashboard.dashboard.js")!);
        var js = await reader.ReadToEndAsync();

        Assert.Contains("body.detail", js);
        Assert.Contains("await problemText(response)", js);
        Assert.False(js.Contains("innerHTML", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- analyzers

    [Fact]
    public async Task A_condition_configured_by_an_extension_method_on_the_builder_is_not_reported()
    {
        var diagnostics = await AnalyzeAsync("""
            public static class Setup
            {
                public static void Configure(IServiceCollection services) => services.AddHomeostat(h => h
                    .AddCondition("feed-down", c => c.Feed().SustainedFor(TimeSpan.FromSeconds(30)))
                    .AddCondition("anything", c => c.SustainedFor(TimeSpan.FromSeconds(30))));
            }

            public static class ConditionExtensions
            {
                public static Homeostat.Conditions.ConditionBuilder Feed(this Homeostat.Conditions.ConditionBuilder c) => c.ForSource("feed");
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HOMEO002", diagnostic.Id);
        Assert.Contains("'anything'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Methods_and_lambdas_that_return_a_task_are_reported_alike()
    {
        var diagnostics = await AnalyzeAsync("""
            public sealed class FeedClient { public Task RenewAsync() => Task.CompletedTask; }

            public sealed class ArrowAction(FeedClient client) : IHealingAction
            {
                public Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken) => client.RenewAsync();
            }

            public sealed class CompletedAction : IHealingAction
            {
                public Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public sealed class FromResultAction : IHealingAction
            {
                public Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken) => Task.FromResult(0);
            }

            public static class Setup
            {
                public static void Configure(IServiceCollection services, FeedClient client) => services.AddHomeostat(h => h
                    .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                        .With("renew", (context, ct) => client.RenewAsync())
                        .Then("done", (context, ct) => Task.CompletedTask)
                        .Then("from-result", (context, ct) => Task.FromResult(true)))));
            }
            """);

        // The arrow-bodied method (its token is named cancellationToken) and the "renew" lambda (ct); not the completed tasks.
        Assert.Equal(["HOMEO003", "HOMEO003"], [.. diagnostics.Select(d => d.Id)]);
        Assert.Single(diagnostics, d => d.GetMessage().Contains("'cancellationToken'", StringComparison.Ordinal));
        Assert.Single(diagnostics, d => d.GetMessage().Contains("'ct'", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- Aspire: root prefix

    [Fact]
    public void A_root_prefix_gives_paths_without_a_double_slash()
    {
        Assert.Equal(string.Empty, HomeostatResourceBuilderExtensions.NormalizePrefix("/"));
        Assert.Equal(string.Empty, HomeostatResourceBuilderExtensions.NormalizePrefix(" / "));
        Assert.Equal("/pause", $"{HomeostatResourceBuilderExtensions.NormalizePrefix("/")}/pause");
        Assert.Equal("https://localhost:7001/dashboard",
            HomeostatResourceBuilderExtensions.DashboardUrl(new Uri("https://localhost:7001/swagger"), HomeostatResourceBuilderExtensions.NormalizePrefix("/")).ToString());
        Assert.Equal("/homeostat", HomeostatResourceBuilderExtensions.NormalizePrefix("/homeostat/"));
        Assert.Throws<ArgumentException>(() => HomeostatResourceBuilderExtensions.NormalizePrefix("/a//b"));
    }

    // ---------------------------------------------------------------- test host disposal

    [Fact]
    public async Task Disposing_the_test_host_twice_does_nothing_the_second_time()
    {
        var host = await HomeostatTestHost.StartAsync(h => h.AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p.With("renew", _ => { }))));

        await host.DisposeAsync();
        await host.DisposeAsync();
    }

    // ---------------------------------------------------------------- health check: no store message in the result

    [Fact]
    public async Task The_health_check_logs_the_store_error_and_does_not_return_it()
    {
        var logs = new CollectingLoggerProvider();
        var store = FailingBudgetStore.Create(new InvalidOperationException("connection refused by redis-01.internal:6379"));
        await using var host = await HomeostatTestHost.StartAsync(
            h => h.UseStore(_ => store),
            services => services
                .AddLogging(b => b.AddProvider(logs))
                .AddHealthChecks().AddHomeostatCheck());
        var checks = host.Services.GetRequiredService<HealthCheckService>();

        var entry = (await checks.CheckHealthAsync()).Entries["homeostat"];
        Assert.Equal(HealthStatus.Degraded, entry.Status);
        Assert.Equal("The Homeostat store is unavailable.", entry.Description);
        Assert.Null(entry.Exception);
        lock (logs.Entries)
        {
            Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Exception?.Message.Contains("redis-01", StringComparison.Ordinal) == true);
        }

        // A second failure within the minute is logged at Debug only.
        entry = (await checks.CheckHealthAsync()).Entries["homeostat"];
        Assert.Null(entry.Exception);
        lock (logs.Entries)
        {
            Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("health check", StringComparison.Ordinal));
        }
    }

    // ---------------------------------------------------------------- helpers

    private const string Usings = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Homeostat;
        using Homeostat.Healing;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;

        """;

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string code)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat([typeof(HomeostatBuilder).Assembly.Location, typeof(HomeostatEndpointRouteBuilderExtensions).Assembly.Location])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "Sample",
            [CSharpSyntaxTree.ParseText(Usings + code, new CSharpParseOptions(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));

        return await compilation.WithAnalyzers([new HomeostatAnalyzer()]).GetAnalyzerDiagnosticsAsync();
    }

    private sealed class FixedHandler(HttpStatusCode status, string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType),
            });
    }

    /// <summary>The in-memory store, except that reading the action budget fails.</summary>
    public class FailingBudgetStore : DispatchProxy
    {
        private IHomeostatStore _inner = null!;
        private Exception _failure = null!;

        public static IHomeostatStore Create(Exception failure)
        {
            var proxy = Create<IHomeostatStore, FailingBudgetStore>();
            var self = (FailingBudgetStore)(object)proxy;
            self._inner = new InMemoryHomeostatStore();
            self._failure = failure;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IHomeostatStore.GetBudgetUsedAsync))
            {
                throw _failure;
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Category, string Message, Exception? Exception)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(CollectingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries)
                {
                    owner.Entries.Add((logLevel, category, formatter(state, exception), exception));
                }
            }
        }
    }
}
