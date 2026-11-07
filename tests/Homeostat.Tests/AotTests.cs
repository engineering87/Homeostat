using System.Text.Json;
using System.Text.Json.Nodes;
using Homeostat.Http;
using Homeostat.Notifications;

namespace Homeostat.Tests;

/// <summary>Serialization paths that must not need reflection, so trimmed and Native AOT applications can use them.</summary>
public class AotTests
{
    private static readonly HomeostatEvent Event = new HomeostatEvent { Kind = HomeostatEventKind.ConditionActivated, Timestamp = DateTimeOffset.Parse("2026-11-02T08:00:00Z"), Application = "weather-api", Condition = "feed-down", Summary = "Feed down & slow" };

    [Fact]
    public void Webhook_sends_the_event_without_reflection_with_enum_names_and_no_nulls()
    {
        var json = JsonDocument.Parse(WebhookSink.ToNode(Event).ToJsonString(HttpEventSink.Json)).RootElement;

        Assert.Equal("ConditionActivated", json.GetProperty("kind").GetString());
        Assert.Equal("feed-down", json.GetProperty("condition").GetString());
        Assert.False(json.TryGetProperty("step", out _));
        Assert.Contains("Feed down & slow", WebhookSink.ToNode(Event).ToJsonString(HttpEventSink.Json));
    }

    [Fact]
    public void Webhook_payloads_built_as_json_nodes_are_sent_as_they_are()
    {
        var node = new JsonObject { ["text"] = Event.Summary };

        Assert.Same(node, WebhookSink.ToNode(node));
    }

    [Fact]
    public void Webhook_payloads_built_as_anonymous_objects_still_work_with_reflection()
    {
        var json = WebhookSink.ToNode(new { text = Event.Summary, kind = Event.Kind, missing = (string?)null });

        Assert.Equal(Event.Summary, json["text"]!.GetValue<string>());
        Assert.Equal("ConditionActivated", json["kind"]!.GetValue<string>());
        Assert.Null(json["missing"]);
    }

    [Fact]
    public void Webhook_payload_null_is_refused_clearly()
        => Assert.Throws<InvalidOperationException>(() => WebhookSink.ToNode(null));
}
