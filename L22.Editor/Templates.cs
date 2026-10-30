using System;
using L22.Editor.Models;
using L22.Editor.Models.World;

namespace L22.Editor;

// Content seeders for `--new --template <name>`. Each takes a fresh World
// and populates it with cells / corner heights to represent the named
// starting scene.
internal static class Templates
{
    // Fills every cell of an 8×8-chunk world with grass at sea level —
    // the same output `File → New Scene` produces inside the UI.
    public static void FillEmpty(World world)
    {
        world.InitializeDefault(8, 8);
        world.FillAllCellsWithType((byte)TerrainType.Grass);
    }

    // Round grassy island with a sand beach ring sat on open water. Water
    // cells are TerrainType.Unset (zero geometry weight — the game treats
    // them as absent) with elevation 0; sand slopes gently up, grass peaks
    // near the island centre.
    public static void FillDesertIsland(World world)
    {
        const int chunks = 8;
        world.InitializeDefault(chunks, chunks);

        int w = chunks * Chunk.Size;
        float cx = w / 2f;
        float cy = w / 2f;
        float shoreOuter = w * 0.33f;        // open-water edge
        float shoreInner = shoreOuter * 0.88f; // where sand ends, grass starts
        float peak = 0.30f;                   // 0..1 storage = ~4.2 world units

        for (int y = 0; y < w; y++)
        for (int x = 0; x < w; x++)
        {
            float dx = x + 0.5f - cx;
            float dy = y + 0.5f - cy;
            float dist = MathF.Sqrt(dx * dx + dy * dy);

            if (dist > shoreOuter)
            {
                world.SetTerrainType(x, y, (byte)TerrainType.Unset);
                world.SetElevation(x, y, 0f);
            }
            else if (dist > shoreInner)
            {
                world.SetTerrainType(x, y, (byte)TerrainType.Sand);
                float t = 1f - (dist - shoreInner) / (shoreOuter - shoreInner);
                world.SetElevation(x, y, t * 0.04f);
            }
            else
            {
                world.SetTerrainType(x, y, (byte)TerrainType.Grass);
                float t = 1f - dist / shoreInner;        // 0 at inner shore, 1 at centre
                float h = 0.04f + t * t * (peak - 0.04f);
                world.SetElevation(x, y, h);
            }
        }
    }
}
