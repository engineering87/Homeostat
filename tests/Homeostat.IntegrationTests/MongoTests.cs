using Homeostat.Signals;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Homeostat.IntegrationTests;

/// <summary>Against a real MongoDB server: HOMEOSTAT_TEST_MONGODB is its connection string.</summary>
public class MongoTests
{
    [Fact]
    public async Task Ping_and_primary_signals_come_from_a_real_server()
    {
        var connection = Support.Variable("HOMEOSTAT_TEST_MONGODB");
        var signals = new CollectingPublisher();
        var collection = new ServiceCollection().AddLogging();
        collection.AddHomeostat(h => h.AddMongoSignals("archive", o => o.Interval = TimeSpan.FromSeconds(1)));
        collection.AddSingleton<IMongoClient>(sp => new MongoClient(MongoClientSettings.FromConnectionString(connection).AddHomeostatSignals(sp, "archive")));
        collection.AddSingleton<ISignalPublisher>(signals);   // registered last: Homeostat's code publishes here
        await using var services = collection.BuildServiceProvider();

        var client = services.GetRequiredService<IMongoClient>();
        await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));

        await Support.EventuallyAsync(
            () => signals.Latest("mongodb:archive:ping") is { Severity: SignalSeverity.Healthy } && signals.Latest("mongodb:archive:primary") is not null,
            () => Support.DetectAsync(services, signals),
            what: "the ping and primary signals");
        Assert.Equal(SignalSeverity.Healthy, signals.Latest("mongodb:archive:primary")!.Severity);
    }
}
