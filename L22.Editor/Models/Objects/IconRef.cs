namespace L22.Editor.Models.Objects;

// Reference to an icon image + which cell of its grid to use. A plain
// standalone icon is a 1×1 grid with cell (0,0). A sprite sheet uses the
// cols/rows + col/row to pick a sub-rect.
public sealed class IconRef
{
    public string ImagePath { get; set; } = "";
    public int GridCols { get; set; } = 1;
    public int GridRows { get; set; } = 1;
    public int CellCol { get; set; } = 0;
    public int CellRow { get; set; } = 0;
}
