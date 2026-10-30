namespace L22.Editor.Models.World;

public readonly record struct ChunkCoord(int X, int Y)
{
    public override string ToString() => $"({X},{Y})";
}
