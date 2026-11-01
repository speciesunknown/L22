using System.Collections.Generic;
using L22.Editor.Models.Commands;
using L22.Editor.Models.Objects;
using L22.Editor.Models.Tools;
using L22.Editor.Models.World;
using L22.Editor.ViewModels;

namespace L22.Editor;

// Composition root. World/History/CommandHistory are the data layer;
// Brush is the view-model state; ToolManager glues them together (reads VM,
// writes to the data layer only through commands).
public sealed class EditorContext
{
    public World World { get; }
    public CommandHistory History { get; }
    public BrushViewModel Brush { get; }
    public ToolManager Tools { get; }
    // Scene-local object placements. Flat list (no spatial index yet —
    // scenes are small enough that linear scans are fine).
    public List<ObjectInstance> Objects { get; } = new();
    // Shared prefab library for the project. Multiple open scenes may
    // reference the same instance; wire SetLibraryDir at load time.
    public PrefabLibrary Prefabs { get; } = new();

    // Fired after any Objects mutation. Viewports subscribe in
    // BindContext to redraw their object cubes.
    public event System.Action? ObjectsChanged;
    public void RaiseObjectsChanged() => ObjectsChanged?.Invoke();

    // In-viewport object selection. Drag in the top-down with the Object
    // tool active rubber-bands a selection; clicking without a drag still
    // places a new one. SelectionChanged fires after the set mutates.
    public System.Collections.Generic.HashSet<string> SelectedObjectIds { get; } = new();
    public event System.Action? ObjectSelectionChanged;
    public void RaiseObjectSelectionChanged() => ObjectSelectionChanged?.Invoke();

    public EditorContext(string fileId) : this(new World(fileId)) { }

    public EditorContext(World world)
    {
        World = world;
        History = new CommandHistory(World);
        Brush = new BrushViewModel();
        Tools = new ToolManager(World, History, Brush);
    }

    // The currently-active document's context. Mutated by MainViewModel when
    // the user switches tabs. IPC and the sample-scene helper look here.
    public static EditorContext? Current { get; set; }

    public static EditorContext CreateDefault(string fileId)
    {
        var ctx = new EditorContext(fileId);
        ctx.World.InitializeDefault(4, 4);
        Current = ctx;
        return ctx;
    }
}
