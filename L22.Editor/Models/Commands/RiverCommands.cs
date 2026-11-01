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

// Add a branch River to a parent at the parent's selected vertex index.
public sealed class AddBranchCommand : IEditorCommand
{
    private readonly EditorContext _ctx;
    private readonly string _parentId;
    private readonly River _branch;
    public string Name => $"Add branch {_branch.Id} to {_parentId}";

    public AddBranchCommand(EditorContext ctx, string parentId, River branch)
    {
        _ctx = ctx;
        _parentId = parentId;
        _branch = branch;
    }
    public void Execute(World.World world)
    {
        var p = FindTop(_ctx, _parentId);
        if (p is null) return;
        p.Branches.Add(_branch);
        _ctx.RaiseRiversChanged();
    }
    public void Undo(World.World world)
    {
        var p = FindTop(_ctx, _parentId);
        if (p is null) return;
        p.Branches.Remove(_branch);
        _ctx.RaiseRiversChanged();
    }
    private static River? FindTop(EditorContext ctx, string id)
    {
        foreach (var r in ctx.Rivers)
        {
            var hit = FindRecursive(r, id);
            if (hit is not null) return hit;
        }
        return null;
    }
    private static River? FindRecursive(River r, string id)
    {
        if (r.Id == id) return r;
        foreach (var b in r.Branches)
        {
            var hit = FindRecursive(b, id);
            if (hit is not null) return hit;
        }
        return null;
    }
}

// Journey update: dragging a vertex's handle updates its bezier out
// direction. Width stays separate — the slider owns that.
public sealed class SetRiverVertexHandleCommand : IEditorCommand
{
    private readonly EditorContext _ctx;
    private readonly string _riverId;
    private readonly int _index;
    private readonly System.Numerics.Vector2 _before, _after;
    public string Name => "Set river vertex handle";

    public SetRiverVertexHandleCommand(EditorContext ctx, string riverId, int index,
                                        System.Numerics.Vector2 before, System.Numerics.Vector2 after)
    {
        _ctx = ctx; _riverId = riverId; _index = index; _before = before; _after = after;
    }
    public void Execute(World.World world)
    {
        var r = FindAnywhere(_ctx, _riverId);
        if (r is null || _index < 0 || _index >= r.Vertices.Count) return;
        r.Vertices[_index].BezierHandleOut = _after;
        _ctx.RaiseRiversChanged();
    }
    public void Undo(World.World world)
    {
        var r = FindAnywhere(_ctx, _riverId);
        if (r is null || _index < 0 || _index >= r.Vertices.Count) return;
        r.Vertices[_index].BezierHandleOut = _before;
        _ctx.RaiseRiversChanged();
    }
    private static River? FindAnywhere(EditorContext ctx, string id)
    {
        foreach (var r in ctx.Rivers)
        {
            var hit = Recurse(r, id);
            if (hit is not null) return hit;
        }
        return null;
    }
    private static River? Recurse(River r, string id)
    {
        if (r.Id == id) return r;
        foreach (var b in r.Branches)
        {
            var hit = Recurse(b, id);
            if (hit is not null) return hit;
        }
        return null;
    }
}

// Scenario 3: increase the width of the selected vertex changes that
// node *and its two neighbours*. The neighbour effect is at half the
// delta so the taper is smooth.
public sealed class SetRiverVertexWidthCommand : IEditorCommand
{
    private readonly EditorContext _ctx;
    private readonly string _riverId;
    private readonly int _index;
    private readonly float _after;
    private float[] _before = System.Array.Empty<float>();
    public string Name => "Set river vertex width";

    public SetRiverVertexWidthCommand(EditorContext ctx, string riverId, int index, float after)
    {
        _ctx = ctx;
        _riverId = riverId;
        _index = index;
        _after = after;
    }
    public void Execute(World.World world)
    {
        var r = FindRiverAnywhere(_ctx, _riverId);
        if (r is null || _index < 0 || _index >= r.Vertices.Count) return;
        int prev = System.Math.Max(0, _index - 1);
        int next = System.Math.Min(r.Vertices.Count - 1, _index + 1);
        _before = new[] { r.Vertices[prev].WidthWorld, r.Vertices[_index].WidthWorld, r.Vertices[next].WidthWorld };
        float delta = _after - r.Vertices[_index].WidthWorld;
        r.Vertices[_index].WidthWorld = _after;
        if (prev != _index) r.Vertices[prev].WidthWorld = System.Math.Max(0.1f, r.Vertices[prev].WidthWorld + delta * 0.5f);
        if (next != _index) r.Vertices[next].WidthWorld = System.Math.Max(0.1f, r.Vertices[next].WidthWorld + delta * 0.5f);
        _ctx.RaiseRiversChanged();
    }
    public void Undo(World.World world)
    {
        var r = FindRiverAnywhere(_ctx, _riverId);
        if (r is null || _before.Length < 3) return;
        int prev = System.Math.Max(0, _index - 1);
        int next = System.Math.Min(r.Vertices.Count - 1, _index + 1);
        r.Vertices[prev].WidthWorld = _before[0];
        r.Vertices[_index].WidthWorld = _before[1];
        r.Vertices[next].WidthWorld = _before[2];
        _ctx.RaiseRiversChanged();
    }
    private static River? FindRiverAnywhere(EditorContext ctx, string id)
    {
        foreach (var r in ctx.Rivers)
        {
            var hit = FindRecursive(r, id);
            if (hit is not null) return hit;
        }
        return null;
    }
    private static River? FindRecursive(River r, string id)
    {
        if (r.Id == id) return r;
        foreach (var b in r.Branches)
        {
            var hit = FindRecursive(b, id);
            if (hit is not null) return hit;
        }
        return null;
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
