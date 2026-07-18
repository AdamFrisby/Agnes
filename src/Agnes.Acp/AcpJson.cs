using System.Text.Json;

namespace Agnes.Acp;

/// <summary>Shared JSON options matching ACP's wire conventions (camelCase keys).</summary>
internal static class AcpJson
{
    public static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
