using System.Collections.Generic;

namespace L22.Editor.Models.Commands;

public enum CornerApplyMode { Override, Add }

// Chunk-corner elevation command. The affected unit is a chunk corner
// (shared by up to 4 chunks). Values are the elevation delta the Corner
// Elevation tool contributes on top of painted terrain.
public sealed class CornerElevationCommand : IEditorCommand
{
    private readonly Dictionary<(int cx, int cy), (bool hadBefore, float before, float after)> _edits = new();

    public string Name => "Chunk Corner Elevation";
    public int Count => _edits.Count;

    public void Record(World.World world, int cornerCx, int cornerCy, CornerApplyMode mode, float value)
    {
        bool had = world.HasChunkCornerElevation(cornerCx, cornerCy);
        float before = world.GetChunkCornerElevation(cornerCx, cornerCy);
        float after = mode switch
        {
            CornerApplyMode.Override => System.Math.Clamp(value, -4f, 4f),
            _ => System.Math.Clamp(before + value, -4f, 4f),
        };
        _edits[(cornerCx, cornerCy)] = (had, before, after);
        world.SetChunkCornerElevation(cornerCx, cornerCy, after);
    }

    public void Execute(World.World world)
    {
        foreach (var kv in _edits)
            world.SetChunkCornerElevation(kv.Key.cx, kv.Key.cy, kv.Value.after);
    }

    public void Undo(World.World world)
    {
        foreach (var kv in _edits)
        {
            if (kv.Value.hadBefore) world.SetChunkCornerElevation(kv.Key.cx, kv.Key.cy, kv.Value.before);
            else world.ClearChunkCornerElevation(kv.Key.cx, kv.Key.cy);
        }
    }
}
