using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using L22.Editor.Models.Rendering;
using L22.Editor.Models.World;

namespace L22.Editor.Models.Rivers;

// Phase 6 export: writes an export/ sibling folder inside the project
// containing a riverbed mask + a riverbed OBJ mesh. The game reads the
// mask to tell terrain from riverbed per cell and the OBJ to spawn its
// water surface geometry over the sunken channel.
//
// Mask layout: one pixel per world cell inside the bounding box of
// every chunk that touches a river. R channel = 255 inside the river
// band, 0 outside; G channel = 255 at the deepest point (useful for
// flow spawn hints); B channel reserved.
public static class RiverExporter
{
    public const string ExportDirName = "export";
    public const string MaskFileName = "riverbed.png";
    public const string ObjFileName = "riverbed.obj";

    // Vertices are included whenever at least one vertex of their containing
    // chunk cell carries river influence. The result shares vertex XZ with
    // the terrain mesh so the game can snap the water surface to the
    // underlying bank verts without a seam.
    public static void Export(string sceneFolder, World.World world,
                               IReadOnlyList<River> rivers)
    {
        var dir = Path.Combine(sceneFolder, ExportDirName);
        Directory.CreateDirectory(dir);

        var (ox, oy, cw, ch) = GetChunkBounds(world);
        int widthCells = cw * Chunk.Size;
        int heightCells = ch * Chunk.Size;

        var rgba = new byte[widthCells * heightCells * 4];
        var objVerts = new List<Vector3>();
        var objFaces = new List<(int a, int b, int c)>();

        for (int py = 0; py < heightCells; py++)
        for (int px = 0; px < widthCells; px++)
        {
            int wx = ox * Chunk.Size + px;
            int wy = oy * Chunk.Size + py;
            var (jx, jz) = Jitter.Offset(wx, wy);
            float influence = TerrainMeshBuilder.ComputeRiverInfluence(rivers, wx + jx, wy + jz);
            int idx = (py * widthCells + px) * 4;
            byte mask = (byte)System.Math.Clamp((int)(influence * 255f), 0, 255);
            rgba[idx + 0] = mask;
            rgba[idx + 1] = (byte)(influence >= 0.99f ? 255 : 0);
            rgba[idx + 2] = 0;
            rgba[idx + 3] = 255;
        }

        PngWriter.WriteRgba(Path.Combine(dir, MaskFileName), rgba, widthCells, heightCells, flipY: false);

        // Riverbed OBJ: emit a quad per cell whose influence > 0 at any
        // corner. Y is the terrain surface minus the local influence × the
        // same RiverbedDepth constant the mesh builder uses, so the OBJ
        // matches the on-screen mesh exactly.
        var vindex = new Dictionary<(int px, int py), int>();
        int NextVertex(int px, int py)
        {
            if (vindex.TryGetValue((px, py), out var i)) return i;
            int wx = ox * Chunk.Size + px;
            int wy = oy * Chunk.Size + py;
            var (jx, jz) = Jitter.Offset(wx, wy);
            float h = TerrainMeshBuilder.SampleSurfaceY(world, wx, wy);
            float influence = TerrainMeshBuilder.ComputeRiverInfluence(rivers, wx + jx, wy + jz);
            float y = h - influence * TerrainMeshBuilder.RiverbedDepth;
            objVerts.Add(new Vector3(wx + jx, y, wy + jz));
            i = objVerts.Count;  // OBJ is 1-based
            vindex[(px, py)] = i;
            return i;
        }

        for (int py = 0; py < heightCells; py++)
        for (int px = 0; px < widthCells; px++)
        {
            int wx = ox * Chunk.Size + px;
            int wy = oy * Chunk.Size + py;
            float inf00 = InfluenceAt(wx,     wy,     rivers);
            float inf10 = InfluenceAt(wx + 1, wy,     rivers);
            float inf01 = InfluenceAt(wx,     wy + 1, rivers);
            float inf11 = InfluenceAt(wx + 1, wy + 1, rivers);
            if (inf00 <= 0 && inf10 <= 0 && inf01 <= 0 && inf11 <= 0) continue;
            int a = NextVertex(px,     py);
            int b = NextVertex(px + 1, py);
            int c = NextVertex(px,     py + 1);
            int d = NextVertex(px + 1, py + 1);
            objFaces.Add((a, d, b));
            objFaces.Add((a, c, d));
        }

        var sb = new StringBuilder();
        sb.AppendLine("# L22 riverbed export");
        sb.AppendLine($"# vertices: {objVerts.Count}  faces: {objFaces.Count}");
        foreach (var v in objVerts)
            sb.AppendLine(System.FormattableString.Invariant($"v {v.X:0.######} {v.Y:0.######} {v.Z:0.######}"));
        foreach (var f in objFaces)
            sb.AppendLine($"f {f.a} {f.b} {f.c}");
        File.WriteAllText(Path.Combine(dir, ObjFileName), sb.ToString());
    }

    private static float InfluenceAt(int wx, int wy, IReadOnlyList<River> rivers)
    {
        var (jx, jz) = Jitter.Offset(wx, wy);
        return TerrainMeshBuilder.ComputeRiverInfluence(rivers, wx + jx, wy + jz);
    }

    private static (int ox, int oy, int chunksW, int chunksH) GetChunkBounds(World.World world)
    {
        if (world.Chunks.Count == 0) return (0, 0, 1, 1);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var c in world.Chunks.Keys)
        {
            if (c.X < minX) minX = c.X;
            if (c.Y < minY) minY = c.Y;
            if (c.X > maxX) maxX = c.X;
            if (c.Y > maxY) maxY = c.Y;
        }
        return (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
