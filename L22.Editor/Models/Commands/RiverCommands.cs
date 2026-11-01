using System.Collections.Generic;
using L22.Editor.Models.Rivers;

namespace L22.Editor.Models.Commands;

// Add a brand-new River to the scene list.
public sealed class AddRiverCommand : IEditorCommand
{
    private readonly EditorContext _ctx;
    private readonly River _river;
    public string Name => $"Add river {_river.Id}";

    public AddRiverCommand(EditorContext ctx, River river)
    {
        _ctx = ctx;
        _river = river;
    }
    public void Execute(World.World world) { _ctx.Rivers.Add(_river); _ctx.RaiseRiversChanged(); }
    public void Undo(World.World world)    { _ctx.Rivers.Remove(_river); _ctx.RaiseRiversChanged(); }
}

public sealed class DeleteRiverCommand : IEditorCommand
{
    private readonly EditorContext _ctx;
    private readonly River _river;
    private int _index = -1;
    public string Name => $"Delete river {_river.Id}";

    public DeleteRiverCommand(EditorContext ctx, River river)
    {
        _ctx = ctx;
        _river = river;
    }
    public void Execute(World.World world)
    {
        _index = _ctx.Rivers.IndexOf(_river);
        if (_index >= 0) _ctx.Rivers.RemoveAt(_index);
        _ctx.RaiseRiversChanged();
    }
    public void Undo(World.World world)
    {
        if (_index < 0 || _index > _ctx.Rivers.Count) _ctx.Rivers.Add(_river);
        else _ctx.Rivers.Insert(_index, _river);
        _ctx.RaiseRiversChanged();
    }
}

// Append a vertex to an existing river. Target is identified by Id so
// undo/redo survives even if the river reference bounces through
// serialization in a future command stack migration.
public sealed class AddRiverVertexCommand : IEditorCommand
{
    private readonly EditorContext _ctx;
    private readonly string _riverId;
    private readonly RiverVertex _vertex;
    public string Name => $"Add river vertex ({_vertex.CellX}, {_vertex.CellY})";

    public AddRiverVertexCommand(EditorContext ctx, string riverId, RiverVertex vertex)
    {
        _ctx = ctx;
        _riverId = riverId;
        _vertex = vertex;
    }
    public void Execute(World.World world)
    {
        var r = FindRiver();
        if (r is null) return;
        r.Vertices.Add(_vertex);
        _ctx.RaiseRiversChanged();
    }
    public void Undo(World.World world)
    {
        var r = FindRiver();
        if (r is null) return;
        r.Vertices.Remove(_vertex);
        _ctx.RaiseRiversChanged();
    }
    private River? FindRiver()
    {
        foreach (var r in _ctx.Rivers)
            if (r.Id == _riverId) return r;
        return null;
    }
}
