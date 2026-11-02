using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Homeostat.Notifications;
using Homeostat.Persistence;
using Homeostat.Recording;

namespace Homeostat;

/// <summary>
/// Serialization metadata generated at compile time for the types Homeostat writes (stores, recordings, audit log,
/// baselines), so it needs no reflection and works with trimming and Native AOT.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(FileHomeostatStore.Document))]
[JsonSerializable(typeof(HealingRunRecord))]
[JsonSerializable(typeof(ConditionEventRecord))]
[JsonSerializable(typeof(RecordedSignal))]
[JsonSerializable(typeof(HomeostatEvent))]
[JsonSerializable(typeof(Conditions.SeasonalBaseline.State))]
internal sealed partial class HomeostatJsonContext : JsonSerializerContext
{
    /// <summary>Options for Homeostat's own types: web defaults, enums as strings, generated metadata only.</summary>
    internal static JsonSerializerOptions CreateOptions(Action<JsonSerializerOptions>? configure = null)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = Default };
        configure?.Invoke(options);
        options.MakeReadOnly();
        return options;
    }
}

internal static class JsonOptionsExtensions
{
    /// <summary>The metadata of <typeparamref name="T"/>, for the serializer overloads that need no reflection.</summary>
    internal static JsonTypeInfo<T> TypeInfo<T>(this JsonSerializerOptions options) => (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}
