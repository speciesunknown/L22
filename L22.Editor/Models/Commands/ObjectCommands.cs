using System;
using System.Collections.Generic;
using L22.Editor.Models.Objects;

namespace L22.Editor.Models.Commands;

// Undoable add/remove of an ObjectInstance. These commands mutate
// EditorContext.Objects, not the World — but they still live on the
// shared CommandHistory so Ctrl+Z crosses the terrain / object boundary.
public sealed class AddObjectCommand : IEditorCommand
{
    private readonly ObjectInstance _instance;
    private readonly EditorContext _ctx;

    public string Name => $"Add object {_instance.Id}";

    public AddObjectCommand(EditorContext ctx, ObjectInstance instance)
    {
        _ctx = ctx;
        _instance = instance;
    }

    public void Execute(World.World world)
    {
        _ctx.Objects.Add(_instance);
        _ctx.RaiseObjectsChanged();
    }

    public void Undo(World.World world)
    {
        _ctx.Objects.Remove(_instance);
        _ctx.RaiseObjectsChanged();
    }
}

public sealed class DeleteObjectCommand : IEditorCommand
{
    private readonly EditorContext _ctx;
    private readonly ObjectInstance _instance;
    private int _index = -1;

    public string Name => $"Delete object {_instance.Id}";

    public DeleteObjectCommand(EditorContext ctx, ObjectInstance instance)
    {
        _ctx = ctx;
        _instance = instance;
    }

    public void Execute(World.World world)
    {
        _index = _ctx.Objects.IndexOf(_instance);
        if (_index >= 0) _ctx.Objects.RemoveAt(_index);
        _ctx.RaiseObjectsChanged();
    }

    public void Undo(World.World world)
    {
        if (_index < 0 || _index > _ctx.Objects.Count) _ctx.Objects.Add(_instance);
        else _ctx.Objects.Insert(_index, _instance);
        _ctx.RaiseObjectsChanged();
    }
}
