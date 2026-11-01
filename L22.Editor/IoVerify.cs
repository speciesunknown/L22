using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using L22.Editor.Models;
using L22.Editor.Models.Objects;
using L22.Editor.Models.Rivers;
using L22.Editor.Models.World;

namespace L22.Editor;

// Headless round-trip test for ProjectIO. Build a World with painted content
// across multiple chunks and chunk-corner elevations, save to a temp file,
// reload, cell-by-cell compare. Exit 0 on OK, 1 on mismatch. Used by
// `L22.Editor --verify-io`.
internal static class IoVerify
{
    public static int Run()
    {
        try
        {
            var world = BuildTestWorld("source");
            var objects = BuildTestObjects();
            var rivers = BuildTestRivers();
            var tmpPath = Path.Combine(Path.GetTempPath(),
                $"l22-io-{Guid.NewGuid():N}.l22proj");

            ProjectIO.Save(world, DocumentKind.Scene, tmpPath, objects, rivers);
            long folderBytes = DirectorySize(tmpPath);
            int fileCount = Directory.GetFiles(tmpPath, "*", SearchOption.AllDirectories).Length;

            var reloaded = ProjectIO.Load(tmpPath, "reload");
            Directory.Delete(tmpPath, recursive: true);

            string? reason = CompareWorlds(world, reloaded.World);
            if (reloaded.Kind != DocumentKind.Scene)
                reason ??= $"kind mismatch: expected Scene, got {reloaded.Kind}";
            reason ??= CompareObjects(objects, reloaded.Objects);
            reason ??= CompareRivers(rivers, reloaded.Rivers);
            if (reason is not null)
            {
                Console.Error.WriteLine($"FAIL: {reason}");
                return 1;
            }

            Console.Out.WriteLine($"OK: round-trip clean. folder bytes = {folderBytes}, "
                                + $"files = {fileCount}, chunks = {world.Chunks.Count}, "
                                + $"objects = {objects.Count}, rivers = {rivers.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static long DirectorySize(string dir)
    {
        long sum = 0;
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            sum += new FileInfo(f).Length;
        return sum;
    }

    // Exercise every PropertyKind, a nested properties tree, an array, an
    // icon with a non-default grid cell, and overrides on each field so a
    // regression in sparse-override handling shows up.
    private static List<ObjectInstance> BuildTestObjects()
    {
        return new List<ObjectInstance>
        {
            new ObjectInstance
            {
                Id = "inst-slime-1",
                PrefabId = "enemy-slime",
                CellX = 10, CellY = 20,
                YOffsetWorld = 0.5f,
                NameOverride = "Slime King",
                PropertyOverrides =
                {
                    new PropertyNode { Name = "hp", Kind = PropertyKind.Number, NumberValue = 42 },
                    new PropertyNode { Name = "loot", Kind = PropertyKind.Array, Children = new List<PropertyNode>
                    {
                        new PropertyNode { Name = "", Kind = PropertyKind.Text, TextValue = "gold" },
                        new PropertyNode { Name = "", Kind = PropertyKind.Text, TextValue = "potion" },
                    } },
                    new PropertyNode { Name = "ai", Kind = PropertyKind.Properties, Children = new List<PropertyNode>
                    {
                        new PropertyNode { Name = "aggro", Kind = PropertyKind.Number, NumberValue = 0.8 },
                        new PropertyNode { Name = "patrol", Kind = PropertyKind.Text, TextValue = "circle" },
                    } },
                },
            },
            new ObjectInstance
            {
                Id = "inst-chest-1",
                PrefabId = "chest",
                CellX = 50, CellY = 50,
                IconOverride = new IconRef { ImagePath = "icons/chest.png", GridCols = 4, GridRows = 2, CellCol = 2, CellRow = 1 },
            },
        };
    }

    private static string? CompareObjects(List<ObjectInstance> a, List<ObjectInstance> b)
    {
        if (a.Count != b.Count) return $"object count differs: {a.Count} vs {b.Count}";
        var opts = new JsonSerializerOptions { WriteIndented = false };
        for (int i = 0; i < a.Count; i++)
        {
            var sa = JsonSerializer.Serialize(a[i], opts);
            var sb = JsonSerializer.Serialize(b[i], opts);
            if (sa != sb) return $"object #{i} differs: {sa} vs {sb}";
        }
        return null;
    }

    // Two rivers; one with a branch; vertices carry sub-cell offsets +
    // custom widths + bezier handles so a regression anywhere in the
    // round-trip surfaces.
    private static List<River> BuildTestRivers()
    {
        return new List<River>
        {
            new River
            {
                Id = "river-main",
                Name = "Main river",
                Vertices =
                {
                    new RiverVertex { CellX = 10, CellY = 10, SubCellX = 0.25f, SubCellY = 0.5f,
                                      WidthWorld = 2.0f,
                                      BezierHandleIn = new Vector2(-0.3f, 0f),
                                      BezierHandleOut = new Vector2(0.4f, 0.1f) },
                    new RiverVertex { CellX = 20, CellY = 15, SubCellX = 0.5f,  SubCellY = 0.5f,
                                      WidthWorld = 2.5f,
                                      BezierHandleIn = new Vector2(-0.5f, 0.1f),
                                      BezierHandleOut = new Vector2(0.5f, -0.1f) },
                    new RiverVertex { CellX = 30, CellY = 30, SubCellX = 0.5f,  SubCellY = 0.5f,
                                      WidthWorld = 3.0f },
                },
                Branches =
                {
                    new River
                    {
                        Id = "river-main-branch-1",
                        Name = "North branch",
                        ForkFromIndex = 1,
                        Vertices =
                        {
                            new RiverVertex { CellX = 20, CellY = 15, SubCellX = 0.5f, SubCellY = 0.5f, WidthWorld = 1.5f },
                            new RiverVertex { CellX = 25, CellY =  5, SubCellX = 0.5f, SubCellY = 0.5f, WidthWorld = 1.0f },
                        },
                    },
                },
            },
            new River
            {
                Id = "river-stream",
                Name = "Side stream",
                Vertices =
                {
                    new RiverVertex { CellX = 50, CellY = 60, WidthWorld = 1.0f },
                    new RiverVertex { CellX = 55, CellY = 70, WidthWorld = 1.0f },
                },
            },
        };
    }

    // Direct field compare — a JSON-string compare masks the Vector2
    // round-trip bug the audit found (both sides empty → strings match
    // → test passes while data is silently lost).
    private static string? CompareRivers(List<River> a, List<River> b)
    {
        if (a.Count != b.Count) return $"river count differs: {a.Count} vs {b.Count}";
        for (int i = 0; i < a.Count; i++)
        {
            var r = CompareRiver(a[i], b[i], $"#{i}");
            if (r is not null) return r;
        }
        return null;
    }

    private static string? CompareRiver(River a, River b, string path)
    {
        if (a.Id != b.Id) return $"river {path}.Id differs: {a.Id} vs {b.Id}";
        if (a.Name != b.Name) return $"river {path}.Name differs: {a.Name} vs {b.Name}";
        if (a.ForkFromIndex != b.ForkFromIndex) return $"river {path}.ForkFromIndex differs: {a.ForkFromIndex} vs {b.ForkFromIndex}";
        if (a.Vertices.Count != b.Vertices.Count) return $"river {path}.Vertices count differs: {a.Vertices.Count} vs {b.Vertices.Count}";
        for (int i = 0; i < a.Vertices.Count; i++)
        {
            var va = a.Vertices[i];
            var vb = b.Vertices[i];
            if (va.CellX != vb.CellX)             return $"river {path}.Vertices[{i}].CellX differs";
            if (va.CellY != vb.CellY)             return $"river {path}.Vertices[{i}].CellY differs";
            if (va.SubCellX != vb.SubCellX)       return $"river {path}.Vertices[{i}].SubCellX differs";
            if (va.SubCellY != vb.SubCellY)       return $"river {path}.Vertices[{i}].SubCellY differs";
            if (va.WidthWorld != vb.WidthWorld)   return $"river {path}.Vertices[{i}].WidthWorld differs";
            if (va.BezierHandleIn != vb.BezierHandleIn)
                return $"river {path}.Vertices[{i}].BezierHandleIn differs: {va.BezierHandleIn} vs {vb.BezierHandleIn}";
            if (va.BezierHandleOut != vb.BezierHandleOut)
                return $"river {path}.Vertices[{i}].BezierHandleOut differs: {va.BezierHandleOut} vs {vb.BezierHandleOut}";
        }
        if (a.Branches.Count != b.Branches.Count) return $"river {path}.Branches count differs: {a.Branches.Count} vs {b.Branches.Count}";
        for (int i = 0; i < a.Branches.Count; i++)
        {
            var r = CompareRiver(a.Branches[i], b.Branches[i], $"{path}.Branches[{i}]");
            if (r is not null) return r;
        }
        return null;
    }

    private static World BuildTestWorld(string fileId)
    {
        var w = new World(fileId);
        w.InitializeDefault(3, 2);

        // Varied terrain so an off-by-one error in row/column order is visible.
        for (int cx = 0; cx < 3 * Chunk.Size; cx++)
            for (int cy = 0; cy < 2 * Chunk.Size; cy++)
            {
                byte t = (byte)(((cx * 7 + cy * 13) % 4));  // 0..3 covers all types
                w.SetTerrainType(cx, cy, t);
                float e = ((cx * 31 + cy * 17) % 100) / 100f;
                w.SetElevation(cx, cy, e);
            }

        w.SetChunkCornerElevation(0, 0, 0.25f);
        w.SetChunkCornerElevation(3, 2, 0.75f);
        w.SetChunkCornerElevation(1, 1, -0.5f);
        return w;
    }

    private static string? CompareWorlds(World a, World b)
    {
        if (a.Chunks.Count != b.Chunks.Count)
            return $"chunk count differs: {a.Chunks.Count} vs {b.Chunks.Count}";

        // Elevation is quantised to 8 bits (0..255) by the folder format,
        // so the round-trip can only be exact modulo 1/255.
        const float elevTolerance = 1f / 255f + 1e-6f;

        foreach (var kv in a.Chunks)
        {
            var cb = b.TryGetChunk(kv.Key);
            if (cb is null) return $"missing chunk {kv.Key} in reloaded world";
            var ca = kv.Value;
            for (int ly = 0; ly < Chunk.Size; ly++)
                for (int lx = 0; lx < Chunk.Size; lx++)
                {
                    if (ca.TerrainType[lx, ly] != cb.TerrainType[lx, ly])
                        return $"terrain mismatch at chunk {kv.Key} local ({lx},{ly}): "
                             + $"{ca.TerrainType[lx, ly]} vs {cb.TerrainType[lx, ly]}";
                    float diff = MathF.Abs(ca.Elevation[lx, ly] - cb.Elevation[lx, ly]);
                    if (diff > elevTolerance)
                        return $"elevation mismatch at chunk {kv.Key} local ({lx},{ly}): "
                             + $"{ca.Elevation[lx, ly]} vs {cb.Elevation[lx, ly]} (diff {diff})";
                }
        }

        foreach (var c in a.EnumerateSetChunkCorners())
        {
            if (!b.HasChunkCornerElevation(c.cx, c.cy))
                return $"missing corner ({c.cx},{c.cy}) in reloaded world";
            float va = a.GetChunkCornerElevation(c.cx, c.cy);
            float vb = b.GetChunkCornerElevation(c.cx, c.cy);
            if (va != vb) return $"corner ({c.cx},{c.cy}) value differs: {va} vs {vb}";
        }

        int bCornerCount = 0;
        foreach (var _ in b.EnumerateSetChunkCorners()) bCornerCount++;
        int aCornerCount = 0;
        foreach (var _ in a.EnumerateSetChunkCorners()) aCornerCount++;
        if (aCornerCount != bCornerCount)
            return $"corner count differs: {aCornerCount} vs {bCornerCount}";

        return null;
    }
}
