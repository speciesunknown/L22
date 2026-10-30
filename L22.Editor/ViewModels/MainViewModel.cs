using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using L22.Editor.Models;
using L22.Editor.Models.Commands;
using L22.Editor.Models.World;

namespace L22.Editor.ViewModels;

// Top-level VM. Owns the set of open documents and tracks the active one.
// All data-layer mutations funnel through commands via the active doc's
// EditorContext.
public sealed class MainViewModel : INotifyPropertyChanged
{
    public ObservableCollection<OpenDocument> Documents { get; } = new();
    public ObservableCollection<RecentEntry> RecentScenes { get; } = new();

    public bool HasDocuments => Documents.Count > 0;

    private OpenDocument? _active;
    public OpenDocument? Active
    {
        get => _active;
        set
        {
            if (ReferenceEquals(_active, value)) return;
            if (_active is not null) _active.Context.History.Changed -= OnHistoryChanged;
            _active = value;
            if (_active is not null)
            {
                EditorContext.Current = _active.Context;
                _active.Context.History.Changed += OnHistoryChanged;
            }
            RaiseAll();
        }
    }

    public MainViewModel()
    {
        Documents.CollectionChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDocuments)));
    }

    public OpenDocument NewDocument(DocumentKind kind, string name)
    {
        var ctx = new EditorContext(name);
        ctx.World.InitializeDefault(4, 4);
        // Give the new scene a visible green ground. Specified by
        // Journeys/001-new-file.md step 2 — a brand-new scene must never
        // read as "nothing here".
        ctx.World.FillAllCellsWithType((byte)Models.World.TerrainType.Grass);
        var doc = new OpenDocument(ctx, kind, name);
        Documents.Add(doc);
        Active = doc;
        return doc;
    }

    public void CloseDocument(OpenDocument doc)
    {
        int i = Documents.IndexOf(doc);
        if (i < 0) return;
        Documents.RemoveAt(i);
        if (ReferenceEquals(_active, doc))
            Active = Documents.Count == 0 ? null : Documents[Math.Min(i, Documents.Count - 1)];
    }

    // Convenience passthroughs — the UI binds to these so it doesn't poke the
    // data layer directly.
    public BrushViewModel? Brush => _active?.Context.Brush;
    public string FileId => _active?.Context.World.FileId ?? "(no doc)";
    public bool CanUndo => _active?.Context.History.CanUndo ?? false;
    public bool CanRedo => _active?.Context.History.CanRedo ?? false;
    public bool IsDirty => _active?.Context.History.Dirty ?? false;

    public void Undo() => _active?.Context.History.Undo();
    public void Redo() => _active?.Context.History.Redo();

    public OpenDocument OpenFromFile(string path)
    {
        var fileId = System.IO.Path.GetFileNameWithoutExtension(path);
        var result = Models.World.ProjectIO.Load(path, fileId);
        var ctx = new EditorContext(result.World);
        ctx.Objects.AddRange(result.Objects);
        ctx.Prefabs.SetLibraryDir(ResolvePrefabLibraryDir(path));
        // Defensive: a freshly-loaded doc should start clean even if some
        // future World-build path pushes commands during load.
        ctx.History.MarkSaved();
        var doc = new OpenDocument(ctx, result.Kind, fileId)
        {
            FilePath = path,
        };
        Documents.Add(doc);
        Active = doc;
        return doc;
    }

    public void SaveActive(string path)
    {
        if (_active is null) return;
        Models.World.ProjectIO.Save(
            _active.Context.World, _active.Kind, path, _active.Context.Objects);
        _active.FilePath = path;
        _active.Name = System.IO.Path.GetFileNameWithoutExtension(path);
        _active.Context.Prefabs.SetLibraryDir(ResolvePrefabLibraryDir(path));
        _active.Context.History.MarkSaved();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
    }

    // Convention: Prefabs/ sits next to the project's parent directory.
    // scene at Game/Levels/scene-3.l22proj → library at Game/Prefabs/.
    // Scenes dropped at odd paths (no "Levels" parent) still resolve to
    // "<project parent>/Prefabs" which may or may not exist; the library
    // handles the missing case.
    private static string? ResolvePrefabLibraryDir(string scenePath)
    {
        var sceneDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(scenePath));
        if (sceneDir is null) return null;
        var parent = System.IO.Path.GetDirectoryName(sceneDir);
        return parent is null ? null : System.IO.Path.Combine(parent, "Prefabs");
    }

    public void SetActiveLayer(LayerKind layer)
    {
        if (Brush is not null) Brush.ActiveLayer = layer;
    }

    public void PickTerrainType(TerrainType type)
    {
        if (Brush is not null) Brush.TerrainType = (byte)type;
    }

    private void OnHistoryChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUndo)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRedo)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
    }

    private void RaiseAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Active)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Brush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanUndo)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRedo)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
