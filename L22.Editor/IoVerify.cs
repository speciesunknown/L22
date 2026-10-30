using System;
using System.IO;
using L22.Editor.Models;
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
            var tmpPath = Path.Combine(Path.GetTempPath(),
                $"l22-io-{Guid.NewGuid():N}.l22proj");

            ProjectIO.Save(world, DocumentKind.Scene, tmpPath);
            var fileSize = new FileInfo(tmpPath).Length;

            var reloaded = ProjectIO.Load(tmpPath, "reload");
            File.Delete(tmpPath);

            string? reason = CompareWorlds(world, reloaded.World);
            if (reloaded.Kind != DocumentKind.Scene)
                reason ??= $"kind mismatch: expected Scene, got {reloaded.Kind}";
            if (reason is not null)
            {
                Console.Error.WriteLine($"FAIL: {reason}");
                return 1;
            }

            Console.Out.WriteLine($"OK: round-trip clean. file size = {fileSize} bytes, "
                                + $"chunks = {world.Chunks.Count}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
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
                    if (ca.Elevation[lx, ly] != cb.Elevation[lx, ly])
                        return $"elevation mismatch at chunk {kv.Key} local ({lx},{ly}): "
                             + $"{ca.Elevation[lx, ly]} vs {cb.Elevation[lx, ly]}";
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
