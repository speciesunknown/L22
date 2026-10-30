using System;
using System.Collections.Generic;
using System.IO;

namespace L22.Editor.Models.Objects;

// In-memory cache of prefabs loaded from a filesystem directory. Owned by
// EditorContext; multiple scenes targeting the same library share one
// instance. Refresh() rescans the directory and re-fires PrefabsChanged.
public sealed class PrefabLibrary
{
    private readonly Dictionary<string, ObjectPrefab> _byId = new();

    public string? LibraryDir { get; private set; }

    public event Action? PrefabsChanged;

    public IReadOnlyDictionary<string, ObjectPrefab> ById => _byId;
    public IEnumerable<ObjectPrefab> All => _byId.Values;

    public ObjectPrefab? Find(string id)
        => _byId.TryGetValue(id, out var p) ? p : null;

    public void SetLibraryDir(string? dir)
    {
        LibraryDir = dir;
        Refresh();
    }

    public void Refresh()
    {
        _byId.Clear();
        if (!string.IsNullOrEmpty(LibraryDir) && Directory.Exists(LibraryDir))
        {
            foreach (var p in ObjectIO.LoadAllPrefabs(LibraryDir))
                if (!string.IsNullOrEmpty(p.Id))
                    _byId[p.Id] = p;
        }
        PrefabsChanged?.Invoke();
    }

    public void Upsert(ObjectPrefab prefab)
    {
        _byId[prefab.Id] = prefab;
        if (LibraryDir is not null) ObjectIO.SavePrefab(LibraryDir, prefab);
        PrefabsChanged?.Invoke();
    }

    public void Remove(string id)
    {
        if (!_byId.Remove(id)) return;
        if (LibraryDir is not null)
        {
            var path = Path.Combine(LibraryDir, id + ObjectIO.PrefabExtension);
            if (File.Exists(path)) File.Delete(path);
        }
        PrefabsChanged?.Invoke();
    }
}
