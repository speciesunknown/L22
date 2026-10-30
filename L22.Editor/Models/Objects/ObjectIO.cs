using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace L22.Editor.Models.Objects;

// JSON persistence for objects. objects.json lives inside the scene folder;
// each .prefab.json lives in a sibling Prefabs/ library directory.
public static class ObjectIO
{
    public const int CurrentInstancesVersion = 1;
    public const int CurrentPrefabVersion = 1;
    public const string InstancesFileName = "objects.json";
    public const string PrefabExtension = ".prefab.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class InstancesFile
    {
        [JsonPropertyName("version")]   public int Version { get; set; } = CurrentInstancesVersion;
        [JsonPropertyName("instances")] public List<ObjectInstance> Instances { get; set; } = new();
    }

    public static void SaveInstances(string sceneFolder, IReadOnlyList<ObjectInstance> instances)
    {
        var payload = new InstancesFile
        {
            Version = CurrentInstancesVersion,
            Instances = new List<ObjectInstance>(instances),
        };
        File.WriteAllText(Path.Combine(sceneFolder, InstancesFileName),
            JsonSerializer.Serialize(payload, Options));
    }

    public static List<ObjectInstance> LoadInstances(string sceneFolder)
    {
        var path = Path.Combine(sceneFolder, InstancesFileName);
        if (!File.Exists(path)) return new List<ObjectInstance>();
        var payload = JsonSerializer.Deserialize<InstancesFile>(File.ReadAllText(path), Options);
        return payload?.Instances ?? new List<ObjectInstance>();
    }

    private sealed class PrefabFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = CurrentPrefabVersion;
        [JsonPropertyName("prefab")]  public ObjectPrefab Prefab { get; set; } = new();
    }

    public static void SavePrefab(string libraryDir, ObjectPrefab prefab)
    {
        Directory.CreateDirectory(libraryDir);
        var file = new PrefabFile { Version = CurrentPrefabVersion, Prefab = prefab };
        File.WriteAllText(Path.Combine(libraryDir, prefab.Id + PrefabExtension),
            JsonSerializer.Serialize(file, Options));
    }

    public static ObjectPrefab? LoadPrefab(string path)
    {
        if (!File.Exists(path)) return null;
        var file = JsonSerializer.Deserialize<PrefabFile>(File.ReadAllText(path), Options);
        return file?.Prefab;
    }

    public static List<ObjectPrefab> LoadAllPrefabs(string libraryDir)
    {
        var result = new List<ObjectPrefab>();
        if (!Directory.Exists(libraryDir)) return result;
        foreach (var file in Directory.EnumerateFiles(libraryDir, "*" + PrefabExtension))
        {
            var p = LoadPrefab(file);
            if (p is not null) result.Add(p);
        }
        return result;
    }
}
