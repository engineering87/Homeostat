using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Homeostat.Healing;
using Homeostat.Signals;
using Homeostat.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.Tests;

public class DataTests
{
    [Fact]
    public async Task Database_probe_opens_runs_the_query_and_disposes_the_connection()
    {
        var log = new List<string>();
        var (services, detector) = ProbeTests.Build(h => h.AddDatabaseProbe("catalog-db", _ => new FakeConnection(log), p =>
        {
            p.CommandText = "SELECT 1 FROM DUAL";
            p.Timeout = TimeSpan.FromSeconds(3);
        }));
        await using var _ = services;
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);

        Assert.Equal(["open", "execute SELECT 1 FROM DUAL timeout=3", "dispose"], log);
        var signal = signals.Signals.Single();
        Assert.Equal(SignalSeverity.Healthy, signal.Severity);
        Assert.True(signal.Value >= 0);
        Assert.Equal("database-probe", signal.Tags!["origin"]);
    }

    [Fact]
    public async Task Database_probe_reports_transient_failures()
    {
        var log = new List<string>();
        var (services, detector) = ProbeTests.Build(h => h.AddDatabaseProbe("catalog-db", _ => new FakeConnection(log) { FailOpen = true }));
        await using var _ = services;
        var signals = new CollectingPublisher();

        await detector.DetectAsync(signals, CancellationToken.None);

        var signal = signals.Signals.Single();
        Assert.Equal(SignalSeverity.Unhealthy, signal.Severity);
        Assert.Equal("true", signal.Tags!["transient"]);
        Assert.Contains("dispose", log);
    }

    [Fact]
    public async Task Database_probe_without_a_query_only_opens_the_connection()
    {
        var log = new List<string>();
        var (services, detector) = ProbeTests.Build(h => h.AddDatabaseProbe("catalog-db", _ => new FakeConnection(log), p => p.CommandText = null));
        await using var _ = services;

        await detector.DetectAsync(new CollectingPublisher(), CancellationToken.None);

        Assert.Equal(["open", "dispose"], log);
    }

    [Fact]
    public async Task ClearConnectionPools_calls_the_provider_method()
    {
        FakeConnection.Cleared = 0;
        AsyncPoolConnection.Cleared = 0;
        await using var host = await HomeostatTestHost.StartAsync(h => h
            .AddCondition("db-stale", c => c.ForSource("catalog-db").Heal(p => p
                .ClearConnectionPools<FakeConnection>()
                .ClearConnectionPools<AsyncPoolConnection>()
                .VerifyWith<NotHealedOnce>())));

        host.Publish("catalog-db", SignalSeverity.Unhealthy);
        var run = await host.WaitForRunAsync("db-stale");

        Assert.Equal(HealingRunOutcome.Healed, run.Outcome);
        Assert.Equal(["Clear FakeConnection pools", "Clear AsyncPoolConnection pools"], run.Steps.Select(s => s.Name));
        Assert.Equal(1, FakeConnection.Cleared);
        Assert.Equal(1, AsyncPoolConnection.Cleared);
    }

    [Fact]
    public void ClearConnectionPools_rejects_providers_without_pools()
    {
        var services = new ServiceCollection();
        services.AddHomeostat(h => h.AddCondition("db", c => c.ForSource("db").Heal(p =>
        {
            Assert.Throws<InvalidOperationException>(() => p.ClearConnectionPools<NoPoolConnection>());
            p.With("ok", _ => { });
        })));
    }

    /// <summary>Not healed after the first step, healed after the second.</summary>
    internal sealed class NotHealedOnce : IHealingVerifier
    {
        public Task<VerificationOutcome> VerifyAsync(HealingContext context, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(context.Step == 0 ? VerificationOutcome.NotHealed : VerificationOutcome.Healed);
    }

    internal class FakeConnection(List<string>? log = null) : DbConnection
    {
        public static int Cleared;
        private ConnectionState _state = ConnectionState.Closed;

        public bool FailOpen { get; init; }

        public static void ClearAllPools() => Interlocked.Increment(ref Cleared);

        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "fake";

        public override string DataSource => "fake";

        public override string ServerVersion => "1.0";

        public override ConnectionState State => _state;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() => _state = ConnectionState.Closed;

        public override void Open()
        {
            if (FailOpen)
            {
                throw new ProbeTests.FakeDbException(transient: true);
            }

            log?.Add("open");
            _state = ConnectionState.Open;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => new FakeCommand(log);

        protected override void Dispose(bool disposing)
        {
            log?.Add("dispose");
            base.Dispose(disposing);
        }
    }

    internal sealed class AsyncPoolConnection : FakeConnection
    {
        public static new int Cleared;

        public static Task ClearAllPoolsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Cleared);
            return Task.CompletedTask;
        }
    }

    internal sealed class NoPoolConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "none";

        public override string DataSource => "none";

        public override string ServerVersion => "1.0";

        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class FakeCommand(List<string>? log) : DbCommand
    {
        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;

        public override int CommandTimeout { get; set; }

        public override CommandType CommandType { get; set; }

        public override bool DesignTimeVisible { get; set; }

        public override UpdateRowSource UpdatedRowSource { get; set; }

        protected override DbConnection? DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();

        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel()
        {
        }

        public override int ExecuteNonQuery() => throw new NotSupportedException();

        public override object? ExecuteScalar()
        {
            log?.Add($"execute {CommandText} timeout={CommandTimeout}");
            return 1;
        }

        public override void Prepare()
        {
        }

        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();

        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }
}
