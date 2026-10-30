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

    public void Undo() => _active?.Context.History.Undo();
    public void Redo() => _active?.Context.History.Redo();

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
