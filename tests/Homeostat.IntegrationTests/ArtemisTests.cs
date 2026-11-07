using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.IntegrationTests;

/// <summary>
/// Against a real ActiveMQ Artemis broker: HOMEOSTAT_TEST_ARTEMIS is its web console address (http://localhost:8161),
/// with the user and password of the image (artemis / artemis unless HOMEOSTAT_TEST_ARTEMIS_USER and
/// HOMEOSTAT_TEST_ARTEMIS_PASSWORD say otherwise).
/// </summary>
public class ArtemisTests
{
    [Fact]
    public async Task Queue_signals_read_the_broker_through_jolokia()
    {
        var console = new Uri(Support.Variable("HOMEOSTAT_TEST_ARTEMIS"));
        var user = Environment.GetEnvironmentVariable("HOMEOSTAT_TEST_ARTEMIS_USER") ?? "artemis";
        var password = Environment.GetEnvironmentVariable("HOMEOSTAT_TEST_ARTEMIS_PASSWORD") ?? "artemis";
        var queue = Support.Unique("homeostat-it");

        using var http = new HttpClient { BaseAddress = console };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}")));
        http.DefaultRequestHeaders.TryAddWithoutValidation("Origin", $"{console.Scheme}://{console.Authority}");

        // The broker's name, as the MBean names use it.
        var search = await JolokiaAsync(http, new JsonObject { ["type"] = "search", ["mbean"] = "org.apache.activemq.artemis:broker=*" });
        var mbean = search["value"]!.AsArray()[0]!.GetValue<string>();
        var broker = mbean[(mbean.IndexOf("broker=", StringComparison.Ordinal) + "broker=".Length)..].Trim('"');

        // An anycast queue to watch.
        await JolokiaAsync(http, new JsonObject
        {
            ["type"] = "exec",
            ["mbean"] = mbean,
            ["operation"] = "createQueue(java.lang.String)",
            ["arguments"] = new JsonArray(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["name"] = queue,
                ["address"] = queue,
                ["routing-type"] = "ANYCAST",
                ["durable"] = false,
            })),
        });

        var signals = new CollectingPublisher();
        var collection = new ServiceCollection().AddLogging();
        collection.AddHomeostat(h => h
            .UseArtemis(o =>
            {
                o.ManagementUrl = console;
                o.BrokerName = broker;
                o.UserName = user;
                o.Password = password;
            })
            .AddArtemisQueueSignals(queue, o => o.Interval = TimeSpan.FromSeconds(1)));
        await using var services = collection.BuildServiceProvider();

        await Support.EventuallyAsync(
            () => signals.Latest($"artemis:{queue}:messages")?.Value == 0 && signals.Latest($"artemis:{queue}:consumers")?.Value == 0,
            () => Support.DetectAsync(services, signals),
            what: "the queue signals");
    }

    private static async Task<JsonNode> JolokiaAsync(HttpClient http, JsonObject request)
    {
        using var response = await http.PostAsync("console/jolokia", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"Jolokia answered {(int)response.StatusCode}: {body}");
        var answer = JsonNode.Parse(body)!;
        Assert.True(answer["status"]?.GetValue<int>() == 200, $"Jolokia refused {request.ToJsonString()}: {body}");
        return answer;
    }
}
