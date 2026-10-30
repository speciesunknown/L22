using L22.Editor.Models.Commands;
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

    public EditorContext(string fileId)
    {
        World = new World(fileId);
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
