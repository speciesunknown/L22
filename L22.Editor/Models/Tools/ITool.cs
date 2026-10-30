namespace L22.Editor.Models.Tools;

public interface ITool
{
    string Name { get; }
    void BeginStroke(int worldX, int worldY);
    void ContinueStroke(int worldX, int worldY);
    void EndStroke();
}
