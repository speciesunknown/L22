namespace L22.Editor.Models.World;

public enum LayerKind
{
    TerrainType = 0,
    Elevation = 1,
}

public enum TerrainType : byte
{
    Unset = 0,
    Sand = 1,
    Grass = 2,
    Rock = 3,
}
