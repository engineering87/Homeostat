using System.Collections.Immutable;
using Homeostat.Analyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Homeostat.Tests;

public class AnalyzerTests
{
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

    private static string[] Ids(ImmutableArray<Diagnostic> diagnostics) => [.. diagnostics.Select(d => d.Id).Order()];

    [Fact]
    public async Task Task_returned_through_the_synchronous_overload_is_reported()
    {
        var diagnostics = await AnalyzeAsync("""
            public sealed class FeedClient { public ValueTask RenewAsync() => default; public void Renew() { } }
            public static class Setup
            {
                public static void Configure(IServiceCollection services) => services.AddHomeostat(h => h
                    .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                        .With("renew-lost", sp => sp.GetRequiredService<FeedClient>().RenewAsync())
                        .Then("renew-sync", sp => sp.GetRequiredService<FeedClient>().Renew())
                        .Then("renew-awaited", async sp => await sp.GetRequiredService<FeedClient>().RenewAsync()))));
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HOMEO001", diagnostic.Id);
        Assert.Contains("ValueTask", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Condition_without_source_is_reported_but_filters_and_composites_are_not()
    {
        var diagnostics = await AnalyzeAsync("""
            public static class Setup
            {
                public static void Configure(IServiceCollection services) => services.AddHomeostat(h => h
                    .AddCondition("anything", c => c.SustainedFor(TimeSpan.FromSeconds(30)))
                    .AddCondition("feed-down", c => c.ForSource("feed"))
                    .AddCondition("slow", c => c.Where(s => s.Value > 100))
                    .AddCondition("both", c => c.WhenAllActive("feed-down", "slow"))
                    .AddCondition("helper", c => Conditions.Feed(c)));
            }

            public static class Conditions
            {
                public static void Feed(Homeostat.Conditions.ConditionBuilder c) => c.ForSource("feed");
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HOMEO002", diagnostic.Id);
        Assert.Contains("'anything'", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Actions_that_ignore_their_token_are_reported()
    {
        var diagnostics = await AnalyzeAsync("""
            public sealed class RenewAction : IHealingAction
            {
                public async Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken) => await Task.Delay(1000);
            }

            public sealed class GoodAction : IHealingAction
            {
                public async Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken) => await Task.Delay(1000, cancellationToken);
            }

            public sealed class SyncAction : IHealingAction
            {
                public Task ExecuteAsync(HealingContext context, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public static class Setup
            {
                public static void Configure(IServiceCollection services) => services.AddHomeostat(h => h
                    .AddCondition("feed-down", c => c.ForSource("feed").Heal(p => p
                        .With("wait", async (context, ct) => await Task.Delay(500))
                        .Then("wait-cancellable", async (context, ct) => await Task.Delay(500, ct))
                        .Then("cannot-cancel", async (context, _) => await Task.Delay(500))
                        .Then("returned-task", (context, ct) => Task.Delay(500)))));
            }
            """);

        Assert.Equal(["HOMEO003", "HOMEO003", "HOMEO003"], Ids(diagnostics));
    }

    [Fact]
    public async Task Anonymous_endpoints_are_reported_outside_development()
    {
        var diagnostics = await AnalyzeAsync("""
            public static class Endpoints
            {
                public static void Map(WebApplication app)
                {
                    app.MapHomeostatStatus(allowAnonymous: true);
                    app.MapHomeostatControl("operations");
                    if (app.Environment.IsDevelopment())
                    {
                        app.MapHomeostatDashboard(allowAnonymous: true);
                    }
                    else
                    {
                        app.MapHomeostatAlerts(prefix: "/a", allowAnonymous: true);
                    }

                    if (!app.Environment.IsDevelopment())
                    {
                        app.MapHomeostatAlerts(prefix: "/b", allowAnonymous: true);
                    }
                }
            }
            """);

        Assert.Equal(["HOMEO004", "HOMEO004", "HOMEO004"], Ids(diagnostics));
        var diagnostic = diagnostics[0];
        Assert.Equal("HOMEO004", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
    }

    [Fact]
    public async Task Restarting_a_plain_background_service_is_reported()
    {
        var diagnostics = await AnalyzeAsync("""
            public sealed class PlainWorker : BackgroundService { protected override Task ExecuteAsync(CancellationToken t) => Task.CompletedTask; }
            public sealed class GoodWorker : RestartableBackgroundService { protected override Task RunAsync(CancellationToken t) => Task.CompletedTask; }
            public sealed class Hosted : IHostedService
            {
                public Task StartAsync(CancellationToken t) => Task.CompletedTask;
                public Task StopAsync(CancellationToken t) => Task.CompletedTask;
            }
            public static class Setup
            {
                public static void Configure(IServiceCollection services) => services.AddHomeostat(h => h
                    .AddCondition("stuck", c => c.ForSource("backlog").Heal(p => p
                        .RestartHostedService<PlainWorker>()
                        .Then("x", _ => { })
                        .RestartHostedService<GoodWorker>()
                        .RestartHostedService<Hosted>())));
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("HOMEO005", diagnostic.Id);
        Assert.Contains("PlainWorker", diagnostic.GetMessage());
    }

    [Fact]
    public async Task Code_without_Homeostat_patterns_has_no_diagnostics()
    {
        var diagnostics = await AnalyzeAsync("""
            public static class Other
            {
                public static Task Run(Func<IServiceProvider, Task> work, IServiceProvider sp) => work(sp);
            }
            """);

        Assert.Empty(diagnostics);
    }
}
