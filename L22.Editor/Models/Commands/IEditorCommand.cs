namespace L22.Editor.Models.Commands;

public interface IEditorCommand
{
    string Name { get; }
    void Execute(World.World world);
    void Undo(World.World world);
}
