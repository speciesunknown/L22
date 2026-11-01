using System;
using System.Collections.Generic;
using L22.Editor.Models.Objects;

namespace L22.Editor.Models.Commands;

// (EditPropertyCommand defined below between the add/delete commands.)


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

// Edits a single leaf PropertyNode's value in place. Captures before/after
// so undo restores exactly. Only works for Text/Number leaves; Array /
// Properties nodes aren't scalar-editable.
public sealed class EditPropertyCommand : IEditorCommand
{
    private readonly EditorContext _ctx;
    private readonly PropertyNode _node;
    private readonly string? _beforeText, _afterText;
    private readonly double? _beforeNumber, _afterNumber;

    public string Name => $"Edit {_node.Name}";

    public EditPropertyCommand(EditorContext ctx, PropertyNode node,
                                string? newText, double? newNumber)
    {
        _ctx = ctx;
        _node = node;
        _beforeText = node.TextValue;
        _beforeNumber = node.NumberValue;
        _afterText = newText;
        _afterNumber = newNumber;
    }

    public void Execute(World.World world)
    {
        _node.TextValue = _afterText;
        _node.NumberValue = _afterNumber;
        _ctx.RaiseObjectsChanged();
    }

    public void Undo(World.World world)
    {
        _node.TextValue = _beforeText;
        _node.NumberValue = _beforeNumber;
        _ctx.RaiseObjectsChanged();
    }
}

// Bulk edit of an ObjectInstance's user-visible fields: NameOverride,
// CodeName, and the PropertyOverrides tree. The dialog does the mutation
// locally; this command just records before/after for undo.
public sealed class EditInstanceCommand : IEditorCommand
{
    private readonly EditorContext _ctx;
    private readonly ObjectInstance _instance;
    private readonly string? _beforeName, _afterName;
    private readonly string? _beforeCode, _afterCode;
    private readonly string _beforePrefab, _afterPrefab;
    private readonly List<PropertyNode> _beforeProps, _afterProps;

    public string Name => $"Edit object {_instance.Id}";

    public EditInstanceCommand(EditorContext ctx, ObjectInstance instance,
                               string? newName, string? newCode, string? newPrefabId,
                               List<PropertyNode> newProps)
    {
        _ctx = ctx;
        _instance = instance;
        _beforeName = instance.NameOverride;
        _afterName = newName;
        _beforeCode = instance.CodeName;
        _afterCode = newCode;
        _beforePrefab = instance.PrefabId;
        _afterPrefab = string.IsNullOrEmpty(newPrefabId) ? "none" : newPrefabId!;
        _beforeProps = PropertyNode.DeepClone(instance.PropertyOverrides);
        _afterProps = newProps;
    }

    public void Execute(World.World world)
    {
        _instance.NameOverride = _afterName;
        _instance.CodeName = _afterCode;
        _instance.PrefabId = _afterPrefab;
        _instance.PropertyOverrides = _afterProps;
        _ctx.RaiseObjectsChanged();
    }

    public void Undo(World.World world)
    {
        _instance.NameOverride = _beforeName;
        _instance.CodeName = _beforeCode;
        _instance.PrefabId = _beforePrefab;
        _instance.PropertyOverrides = PropertyNode.DeepClone(_beforeProps);
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
