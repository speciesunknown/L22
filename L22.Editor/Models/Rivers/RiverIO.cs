using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace L22.Editor.Models.Rivers;

// JSON persistence for rivers. rivers.json lives next to objects.json
// inside the scene folder. Branches recurse through the normal River
// serialization — no special handling needed.
public static class RiverIO
{
    public const int CurrentVersion = 1;
    public const string FileName = "rivers.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class RiversFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
        [JsonPropertyName("rivers")]  public List<River> Rivers { get; set; } = new();
    }

    public static void Save(string sceneFolder, IReadOnlyList<River> rivers)
    {
        var payload = new RiversFile
        {
            Version = CurrentVersion,
            Rivers = new List<River>(rivers),
        };
        File.WriteAllText(Path.Combine(sceneFolder, FileName),
            JsonSerializer.Serialize(payload, Options));
    }

    public static List<River> Load(string sceneFolder)
    {
        var path = Path.Combine(sceneFolder, FileName);
        if (!File.Exists(path)) return new List<River>();
        var payload = JsonSerializer.Deserialize<RiversFile>(File.ReadAllText(path), Options);
        return payload?.Rivers ?? new List<River>();
    }
}
