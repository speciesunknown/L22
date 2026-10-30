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
    // Persistent hex code the agent should remember and present as `token`
    // on future connections to skip the PIN dialog.
    [JsonPropertyName("token")] public string? Token { get; set; }
}

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
}
