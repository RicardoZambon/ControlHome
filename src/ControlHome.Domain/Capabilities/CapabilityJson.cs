using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlHome.Domain.Capabilities;

/// <summary>
/// The JSON settings for capability data stored in the database (<c>EndpointCapability.ConstraintsJson</c>,
/// <c>GenericDefinitionJson</c>) and exchanged with connectors: web defaults (camelCase), strict numbers, and polymorphic
/// discriminators (<c>type</c>) accepted anywhere in an object (SQL Server's <c>json</c> type does not promise to keep
/// member order).
/// </summary>
public static class CapabilityJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? Deserialize<T>(JsonElement json) => json.Deserialize<T>(Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.Strict,
            AllowOutOfOrderMetadataProperties = true,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
