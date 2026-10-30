using System;
using L22.Editor.Models.Commands;
using L22.Editor.Models.World;
using L22.Editor.ViewModels;

namespace L22.Editor.Models.Tools;

// Glue between the brush view-model (which the UI mutates freely) and the
// data layer (which only commands mutate). Views pump input through the
// active tool; the tool reads the VM, builds a command, and pushes it to
// the history.
public sealed class ToolManager
{
    public World.World World { get; }
    public CommandHistory History { get; }
    public BrushViewModel Brush { get; }
    public PaintBrushTool Paint { get; }

    public ToolManager(World.World world, CommandHistory history, BrushViewModel brush)
    {
        World = world;
        History = history;
        Brush = brush;
        Paint = new PaintBrushTool(world, history, brush);
    }
}
