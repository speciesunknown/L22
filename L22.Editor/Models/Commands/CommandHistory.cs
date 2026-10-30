using System;
using System.Collections.Generic;

namespace L22.Editor.Models.Commands;

public sealed class CommandHistory
{
    private readonly Stack<IEditorCommand> _undo = new();
    private readonly Stack<IEditorCommand> _redo = new();
    private readonly World.World _world;
    private readonly int _capacity;

    public event Action? Changed;

    public CommandHistory(World.World world, int capacity = 256)
    {
        _world = world;
        _capacity = capacity;
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public string? NextUndoName => _undo.TryPeek(out var c) ? c.Name : null;
    public string? NextRedoName => _redo.TryPeek(out var c) ? c.Name : null;

    // "World has unsaved changes relative to the last Save." Flipped true by
    // any mutation (push/undo/redo); flipped false by MarkSaved().
    public bool Dirty { get; private set; }

    public void MarkSaved()
    {
        if (!Dirty) return;
        Dirty = false;
        Changed?.Invoke();
    }

    // Push a command that was already executed in-place (e.g. during a paint stroke).
    public void PushExecuted(IEditorCommand cmd)
    {
        _undo.Push(cmd);
        _redo.Clear();
        TrimToCapacity();
        Dirty = true;
        Changed?.Invoke();
    }

    public void ExecuteAndPush(IEditorCommand cmd)
    {
        cmd.Execute(_world);
        PushExecuted(cmd);
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var cmd)) return;
        cmd.Undo(_world);
        _redo.Push(cmd);
        Dirty = true;
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var cmd)) return;
        cmd.Execute(_world);
        _undo.Push(cmd);
        Dirty = true;
        Changed?.Invoke();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    private void TrimToCapacity()
    {
        if (_undo.Count <= _capacity) return;
        var kept = new IEditorCommand[_capacity];
        for (int i = _capacity - 1; i >= 0; i--) kept[i] = _undo.Pop();
        _undo.Clear();
        for (int i = 0; i < _capacity; i++) _undo.Push(kept[i]);
    }
}
