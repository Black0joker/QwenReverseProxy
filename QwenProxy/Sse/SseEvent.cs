using System.Text.Json.Nodes;

namespace QwenProxy.Sse;

/// <summary>
/// A structured event emitted by the SSE parsing pipeline (parse_stream /
/// parse_answer). Mirrors the Python event dicts: only the keys that are set
/// appear in the serialized JSON ('type' always; 'value'/'code'/'name' when
/// applicable - including explicit nulls like {"type":"reasoning","value":null}).
/// </summary>
public sealed class SseEvent
{
    public required string Type { get; init; }

    /// <summary>Event payload. Only serialized when HasValue is true.</summary>
    public JsonNode? Value { get; init; }

    public bool HasValue { get; init; }

    /// <summary>Error/warning code.</summary>
    public string? Code { get; init; }

    /// <summary>Function name (function events). Serialized when HasName is true.</summary>
    public string? Name { get; init; }

    public bool HasName { get; init; }

    public static SseEvent Error(string? code, JsonNode? details) => new()
    {
        Type = "error",
        Code = code,
        Value = details,
        HasValue = true,
    };

    public static SseEvent OfValue(string type, JsonNode? value) => new()
    {
        Type = type,
        Value = value,
        HasValue = true,
    };

    public static SseEvent Bare(string type) => new() { Type = type };

    /// <summary>Serialize to the wire format (insertion order: type, code, value, name).</summary>
    public JsonObject ToJson()
    {
        var obj = new JsonObject { ["type"] = Type };
        if (Code is not null) obj["code"] = Code;
        if (HasValue) obj["value"] = Value?.DeepClone();
        if (HasName) obj["name"] = Name;
        return obj;
    }
}
