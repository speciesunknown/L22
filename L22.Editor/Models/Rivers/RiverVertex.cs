using System.Numerics;

namespace L22.Editor.Models.Rivers;

// One control point along a river's path. CellX / CellY pick the grid
// cell the handle "belongs to"; SubCell is the inset within the cell so
// vertices can sit anywhere in world space without being locked to the
// integer grid. WidthWorld is the full bank-to-bank distance in world
// units; BezierHandleIn / Out are relative tangent offsets (world units)
// for the segment that enters and leaves this vertex.
public sealed class RiverVertex
{
    public int CellX { get; set; }
    public int CellY { get; set; }
    public float SubCellX { get; set; } = 0.5f;
    public float SubCellY { get; set; } = 0.5f;
    public float WidthWorld { get; set; } = 1.0f;
    public Vector2 BezierHandleIn { get; set; }
    public Vector2 BezierHandleOut { get; set; }

    // World X / Z the vertex actually sits at, derived from Cell + SubCell.
    public float WorldX => CellX + SubCellX;
    public float WorldZ => CellY + SubCellY;
}
