namespace L22.Editor.Models.World;

public sealed class Chunk
{
    public const int Size = 32;

    public ChunkCoord Coord { get; }
    public byte[,] TerrainType { get; } = new byte[Size, Size];
    public float[,] Elevation { get; } = new float[Size, Size];

    public bool MeshDirty { get; set; } = true;

    public Chunk(ChunkCoord coord)
    {
        Coord = coord;
    }
}
