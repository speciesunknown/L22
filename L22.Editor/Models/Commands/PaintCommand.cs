using System.Collections.Generic;
using L22.Editor.Models.World;

namespace L22.Editor.Models.Commands;

public sealed class TerrainTypePaintCommand : IEditorCommand
{
    private readonly Dictionary<(int x, int y), (byte before, byte after)> _edits = new();

    public string Name => "Paint Terrain Type";
    public int Count => _edits.Count;

    // Capture the pre-stroke value only on first touch of a cell.
    public void Record(World.World world, int x, int y, byte newValue)
    {
        if (!_edits.ContainsKey((x, y)))
        {
            var before = world.GetTerrainType(x, y);
            if (before == newValue) return;
            _edits[(x, y)] = (before, newValue);
        }
        else
        {
            var cur = _edits[(x, y)];
            _edits[(x, y)] = (cur.before, newValue);
        }
        world.SetTerrainType(x, y, newValue);
    }

    public void Execute(World.World world)
    {
        foreach (var kv in _edits)
            world.SetTerrainType(kv.Key.x, kv.Key.y, kv.Value.after);
    }

    public void Undo(World.World world)
    {
        foreach (var kv in _edits)
            world.SetTerrainType(kv.Key.x, kv.Key.y, kv.Value.before);
    }
}

public sealed class ElevationPaintCommand : IEditorCommand
{
    private readonly Dictionary<(int x, int y), (float before, float after)> _edits = new();

    public string Name => "Paint Elevation";
    public int Count => _edits.Count;

    public void Record(World.World world, int x, int y, float newValue)
    {
        if (!_edits.ContainsKey((x, y)))
        {
            var before = world.GetElevation(x, y);
            if (before == newValue) return;
            _edits[(x, y)] = (before, newValue);
        }
        else
        {
            var cur = _edits[(x, y)];
            _edits[(x, y)] = (cur.before, newValue);
        }
        world.SetElevation(x, y, newValue);
    }

    public void Execute(World.World world)
    {
        foreach (var kv in _edits)
            world.SetElevation(kv.Key.x, kv.Key.y, kv.Value.after);
    }

    public void Undo(World.World world)
    {
        foreach (var kv in _edits)
            world.SetElevation(kv.Key.x, kv.Key.y, kv.Value.before);
    }
}
