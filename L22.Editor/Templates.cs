using System;
using System.Collections.Generic;
using L22.Editor.Models;
using L22.Editor.Models.Objects;
using L22.Editor.Models.World;

namespace L22.Editor;

// Content seeders for `--new --template <name>`. Each takes a fresh World
// and populates it with cells / corner heights to represent the named
// starting scene.
internal static class Templates
{
    // Fills every cell of an 8×8-chunk world with grass at sea level —
    // the same output `File → New Scene` produces inside the UI.
    public static void FillEmpty(World world) => FillEmpty(world, out _);

    public static void FillEmpty(World world, out List<ObjectInstance> objects)
    {
        world.InitializeDefault(8, 8);
        world.FillAllCellsWithType((byte)TerrainType.Grass);
        objects = new List<ObjectInstance>();
    }

    public static void FillDesertIsland(World world) => FillDesertIsland(world, out _);

    // Round grassy island with a sand beach ring sat on open water. Water
    // cells are TerrainType.Unset (zero geometry weight — the game treats
    // them as absent) with elevation 0; sand slopes gently up, grass peaks
    // near the island centre.
    public static void FillDesertIsland(World world, out List<ObjectInstance> objects)
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

        // Marker objects so Phase 2 rendering has something to show on load.
        // Each carries sample properties so the property-tree panel has
        // non-empty content for the user to inspect.
        objects = new List<ObjectInstance>
        {
            new ObjectInstance
            {
                Id = "peak-marker", PrefabId = "none", CellX = 64, CellY = 64,
                PropertyOverrides =
                {
                    new PropertyNode { Name = "label", Kind = PropertyKind.Text, TextValue = "Island peak" },
                    new PropertyNode { Name = "elevation_hint", Kind = PropertyKind.Number, NumberValue = 4.2 },
                },
            },
            new ObjectInstance
            {
                Id = "beach-marker", PrefabId = "none", CellX = 80, CellY = 50,
                PropertyOverrides =
                {
                    new PropertyNode { Name = "label", Kind = PropertyKind.Text, TextValue = "North beach" },
                    new PropertyNode { Name = "spawn", Kind = PropertyKind.Properties, Children = new List<PropertyNode>
                    {
                        new PropertyNode { Name = "mob", Kind = PropertyKind.Text, TextValue = "crab" },
                        new PropertyNode { Name = "count", Kind = PropertyKind.Number, NumberValue = 3 },
                    } },
                    new PropertyNode { Name = "loot", Kind = PropertyKind.Array, Children = new List<PropertyNode>
                    {
                        new PropertyNode { Name = "", Kind = PropertyKind.Text, TextValue = "driftwood" },
                        new PropertyNode { Name = "", Kind = PropertyKind.Text, TextValue = "shell" },
                        new PropertyNode { Name = "", Kind = PropertyKind.Text, TextValue = "message_bottle" },
                    } },
                },
            },
            new ObjectInstance
            {
                Id = "water-marker", PrefabId = "none", CellX = 40, CellY = 90,
                PropertyOverrides =
                {
                    new PropertyNode { Name = "label", Kind = PropertyKind.Text, TextValue = "Shipwreck" },
                    new PropertyNode { Name = "sunk", Kind = PropertyKind.Text, TextValue = "true" },
                },
            },
        };
    }
}
