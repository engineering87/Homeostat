using System.Data;
using System.Data.Common;
using Homeostat.Calls;
using Homeostat.EntityFrameworkCore;
using Homeostat.Signals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Tests;

public class EntityFrameworkCoreTests
{
    public sealed class ReportsDbContext(DbContextOptions<ReportsDbContext> options) : DbContext(options);

    [Fact]
    public void The_interceptor_is_added_to_the_context_without_changing_AddDbContext()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ReportsDbContext>(_ => { });
        services.AddHomeostat(h => h.AddDbContextSignals<ReportsDbContext>());
        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<DbContextOptions<ReportsDbContext>>();
        var interceptors = options.FindExtension<CoreOptionsExtension>()!.Interceptors!;
        Assert.Same(provider.GetRequiredKeyedService<HomeostatDbInterceptor>(typeof(ReportsDbContext)), Assert.Single(interceptors));
        Assert.Equal("efcore:ReportsDbContext", provider.GetRequiredKeyedService<CallRecorder>("efcore:ReportsDbContext").Source);
    }

    [Fact]
    public void Registering_the_signals_twice_adds_one_interceptor_with_the_latest_settings()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ReportsDbContext>(_ => { });
        services.AddHomeostat(h => h
            .AddDbContextSignals<ReportsDbContext>()
            .AddDbContextSignals<ReportsDbContext>(o => o.Source = "efcore:reports"));
        var provider = services.BuildServiceProvider();

        var interceptors = provider.GetRequiredService<DbContextOptions<ReportsDbContext>>().FindExtension<CoreOptionsExtension>()!.Interceptors!;
        Assert.Single(interceptors);
        var interceptor = provider.GetRequiredKeyedService<HomeostatDbInterceptor>(typeof(ReportsDbContext));
        interceptor.NonQueryExecuted(null!, Executed(TimeSpan.FromMilliseconds(5)), 1);
        Assert.NotNull(provider.GetRequiredKeyedService<CallRecorder>("efcore:reports").Collect(DateTimeOffset.UtcNow, "efcore"));
    }

    [Fact]
    public async Task Commands_count_as_failures_only_when_the_database_is_in_trouble()
    {
        var recorder = new CallRecorder("efcore:ReportsDbContext", new CallSignalOptions { MinimumCalls = 1 });
        var interceptor = new HomeostatDbInterceptor(recorder, DbContextSignalOptions.IsDatabaseFailure);

        interceptor.NonQueryExecuted(null!, Executed(TimeSpan.FromMilliseconds(12)), 1);
        await interceptor.ScalarExecutedAsync(null!, Executed(TimeSpan.FromMilliseconds(30)), 5);
        interceptor.CommandFailed(null!, Failed(new FakeDbException(transient: true)));
        await interceptor.CommandFailedAsync(null!, Failed(new InvalidOperationException("wrapped", new TimeoutException())));
        interceptor.CommandFailed(null!, Failed(new FakeDbException(transient: false)));    // a constraint violation: the command's fault
        interceptor.ConnectionFailed(null!, new ConnectionErrorEventData(null!, null!, null!, null, Guid.NewGuid(), new InvalidOperationException("pool exhausted"), false, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(15)));

        var signal = recorder.Collect(DateTimeOffset.UtcNow, "efcore")!;
        Assert.Equal("6", signal.Tags!["calls"]);
        Assert.Equal("3", signal.Tags["failures"]);
        Assert.Equal(SignalSeverity.Unhealthy, signal.Severity);    // 50% of the commands failed
    }

    [Fact]
    public void Source_and_classification_can_be_changed()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ReportsDbContext>(_ => { });
        services.AddHomeostat(h => h.AddDbContextSignals<ReportsDbContext>(o =>
        {
            o.Source = "efcore:reports";
            o.IsFailure = _ => true;
        }));
        var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredKeyedService<CallRecorder>("efcore:reports"));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddHomeostat(h => h.AddDbContextSignals<ReportsDbContext>(o => o.IsFailure = null!)));
    }

    private static CommandExecutedEventData Executed(TimeSpan duration)
        => new(null!, null!, null!, null!, "", null, DbCommandMethod.ExecuteNonQuery, Guid.NewGuid(), Guid.NewGuid(), null, false, false, DateTimeOffset.UtcNow, duration, CommandSource.SaveChanges);

    private static CommandErrorEventData Failed(Exception exception)
        => new(null!, null!, null!, null!, "", null, DbCommandMethod.ExecuteNonQuery, Guid.NewGuid(), Guid.NewGuid(), exception, false, false, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1), CommandSource.SaveChanges);

    private sealed class FakeDbException(bool transient) : DbException("database error")
    {
        public override bool IsTransient => transient;
    }
}
