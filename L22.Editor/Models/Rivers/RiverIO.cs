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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new Vector2JsonConverter() },
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
        if (payload is null) return new List<River>();
        if (payload.Version > CurrentVersion)
            throw new InvalidDataException(
                $"unsupported rivers.json version {payload.Version} (editor supports up to {CurrentVersion})");
        var list = payload.Rivers ?? new List<River>();
        foreach (var r in list) Normalise(r);
        return list;
    }

    // Clamp obviously-bad values rather than crashing the overlay / renderer
    // downstream. Branches recurse.
    private static void Normalise(River r)
    {
        if (r.Vertices.Count == 0) r.ForkFromIndex = 0;
        else if (r.ForkFromIndex < 0) r.ForkFromIndex = 0;
        else if (r.ForkFromIndex >= r.Vertices.Count) r.ForkFromIndex = r.Vertices.Count - 1;
        foreach (var v in r.Vertices)
        {
            if (v.SubCellX < 0f) v.SubCellX = 0f; else if (v.SubCellX > 1f) v.SubCellX = 1f;
            if (v.SubCellY < 0f) v.SubCellY = 0f; else if (v.SubCellY > 1f) v.SubCellY = 1f;
            if (v.WidthWorld < 0f) v.WidthWorld = 0f;
        }
        foreach (var b in r.Branches) Normalise(b);
    }
}
