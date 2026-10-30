using System.Text.Json.Serialization;

namespace L22.Editor.Models.Ipc;

// Simple JSON Lines protocol. Every request/response is a single JSON object
// on its own line.

public sealed class HelloRequest
{
    [JsonPropertyName("op")] public string Op { get; set; } = "hello";
    [JsonPropertyName("fileId")] public string FileId { get; set; } = "";
    [JsonPropertyName("pin")] public string Pin { get; set; } = "";
    [JsonPropertyName("agentName")] public string AgentName { get; set; } = "";
    // Persistent hex code issued on previous successful PIN exchange. If
    // present and valid the server skips the dialog + PIN dance.
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}

public sealed class ServerResponse
{
    [JsonPropertyName("op")] public string Op { get; set; } = "ok";
    [JsonPropertyName("msg")] public string? Msg { get; set; }
    [JsonPropertyName("durationHours")] public int? DurationHours { get; set; }
    [JsonPropertyName("fileId")] public string? FileId { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("documents")] public DocumentInfo[]? Documents { get; set; }
    // Object ops: list returns objects; place returns the new id.
    [JsonPropertyName("objects")] public ObjectInfo[]? Objects { get; set; }
    [JsonPropertyName("prefabs")] public PrefabInfo[]? Prefabs { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    // Persistent hex code the agent should remember and present as `token`
    // on future connections to skip the PIN dialog.
    [JsonPropertyName("token")] public string? Token { get; set; }
}

public sealed record ObjectInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("prefabId")] string PrefabId,
    [property: JsonPropertyName("cellX")] int CellX,
    [property: JsonPropertyName("cellY")] int CellY,
    [property: JsonPropertyName("yOffsetWorld")] float YOffsetWorld);

public sealed record PrefabInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("foreignId")] string ForeignId);

public sealed class CommandRequest
{
    [JsonPropertyName("op")] public string Op { get; set; } = "";

    // Paint:
    [JsonPropertyName("layer")] public string? Layer { get; set; }        // "elevation" or "terrainType"
    [JsonPropertyName("x")] public int? X { get; set; }
    [JsonPropertyName("y")] public int? Y { get; set; }
    [JsonPropertyName("radius")] public int? Radius { get; set; }
    [JsonPropertyName("value")] public double? Value { get; set; }        // 0..1 for elevation, byte for type
    [JsonPropertyName("type")] public string? TerrainType { get; set; }   // "sand"|"grass"|"rock"

    // Screenshot:
    [JsonPropertyName("viewport")] public string? Viewport { get; set; }  // "edit" or "free"
    [JsonPropertyName("path")] public string? Path { get; set; }

    // Object ops:
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("prefabId")] public string? PrefabId { get; set; }
    [JsonPropertyName("cellX")] public int? CellX { get; set; }
    [JsonPropertyName("cellY")] public int? CellY { get; set; }
    [JsonPropertyName("yOffsetWorld")] public double? YOffsetWorld { get; set; }

    // Prefab ops:
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("foreignId")]   public string? ForeignId { get; set; }
}
