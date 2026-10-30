using System;
using System.Numerics;
using L22.Editor.Models.World;

namespace L22.Editor.Models.Rendering;

public readonly record struct VertexAttrib(Vector3 Pos, Vector3 Color, Vector3 Normal);

public sealed class ChunkMeshData
{
    public float[] Vertices = Array.Empty<float>();   // interleaved: pos3 + color3 + normal3
    public uint[] Indices = Array.Empty<uint>();      // triangles for cells with terrain
    public uint[] WireIndices = Array.Empty<uint>();  // GL_LINES edges for cells with no terrain
    public int VertexFloatCount => 9;
}

public static class TerrainMeshBuilder
{
    public const float ElevationScale = 14f;

    public static ChunkMeshData Build(World.World world, Chunk chunk)
    {
        int size = Chunk.Size;
        int vside = size + 1;             // 33x33 vertices
        int baseX = chunk.Coord.X * size;
        int baseY = chunk.Coord.Y * size;

        var positions = new Vector3[vside * vside];
        var normals = new Vector3[vside * vside];

        for (int vy = 0; vy < vside; vy++)
        for (int vx = 0; vx < vside; vx++)
        {
            int wx = baseX + vx;
            int wy = baseY + vy;
            var (jx, jz) = Jitter.Offset(wx, wy);
            float h = SampleVertexElevation(world, wx, wy) * ElevationScale;
            positions[vy * vside + vx] = new Vector3(wx + jx, h, wy + jz);
        }

        // Cross-chunk-aware normals: central-difference the elevation sampled
        // through the world (which reads neighbour chunks). Vertices on a
        // chunk's edge see the same gradient as the matching vertex in the
        // adjacent chunk, so lighting stays continuous across seams.
        for (int vy = 0; vy < vside; vy++)
        for (int vx = 0; vx < vside; vx++)
        {
            int wx = baseX + vx;
            int wy = baseY + vy;
            float hL = SampleVertexElevation(world, wx - 1, wy) * ElevationScale;
            float hR = SampleVertexElevation(world, wx + 1, wy) * ElevationScale;
            float hD = SampleVertexElevation(world, wx, wy - 1) * ElevationScale;
            float hU = SampleVertexElevation(world, wx, wy + 1) * ElevationScale;
            var n = new Vector3(hL - hR, 2f, hD - hU);
            normals[vy * vside + vx] = Vector3.Normalize(n);
        }

        // Per-vertex color: average of surrounding cells' terrain-type colors.
        var colors = new Vector3[vside * vside];
        for (int vy = 0; vy < vside; vy++)
        for (int vx = 0; vx < vside; vx++)
        {
            int wx = baseX + vx;
            int wy = baseY + vy;
            colors[vy * vside + vx] = AverageCellColor(world, wx, wy);
        }

        var data = new ChunkMeshData
        {
            Vertices = new float[vside * vside * 9],
        };

        for (int i = 0; i < positions.Length; i++)
        {
            int b = i * 9;
            data.Vertices[b + 0] = positions[i].X;
            data.Vertices[b + 1] = positions[i].Y;
            data.Vertices[b + 2] = positions[i].Z;
            data.Vertices[b + 3] = colors[i].X;
            data.Vertices[b + 4] = colors[i].Y;
            data.Vertices[b + 5] = colors[i].Z;
            data.Vertices[b + 6] = normals[i].X;
            data.Vertices[b + 7] = normals[i].Y;
            data.Vertices[b + 8] = normals[i].Z;
        }

        // All cells get filled triangles so the user keeps seeing the chunk.
        // Unset cells also get wire-line edges on top as a visual cue — their
        // vertex colors are 0,0,0 so a rendered-to-image export reads as
        // "no terrain weighting", which the game treats as absent geometry.
        var tris = new uint[size * size * 6];
        var lines = new System.Collections.Generic.List<uint>(32);
        int ii = 0;
        for (int cy = 0; cy < size; cy++)
        for (int cx = 0; cx < size; cx++)
        {
            uint i00 = (uint)(cy * vside + cx);
            uint i10 = (uint)(cy * vside + (cx + 1));
            uint i01 = (uint)((cy + 1) * vside + cx);
            uint i11 = (uint)((cy + 1) * vside + (cx + 1));
            tris[ii++] = i00; tris[ii++] = i11; tris[ii++] = i10;
            tris[ii++] = i00; tris[ii++] = i01; tris[ii++] = i11;

            if (chunk.TerrainType[cx, cy] == (byte)Models.World.TerrainType.Unset)
            {
                lines.Add(i00); lines.Add(i10);
                lines.Add(i10); lines.Add(i11);
                lines.Add(i11); lines.Add(i01);
                lines.Add(i01); lines.Add(i00);
            }
        }
        data.Indices = tris;
        data.WireIndices = lines.ToArray();

        return data;
    }

    // World-space Y of the terrain surface at (wx, wy). Thin wrapper over
    // the per-vertex elevation sampler the mesh uses, scaled to world units
    // — object placements use this to sit on the ground.
    public static float SampleSurfaceY(World.World world, int wx, int wy)
        => SampleVertexElevation(world, wx, wy) * ElevationScale;

    // Final vertex elevation = chunk-corner contribution (bilinear across
    // the 4 chunk corners the vertex sits inside) + terrain elevation
    // (per-cell, averaged across the up to four cells touching this vertex,
    // from the paint brush).
    private static float SampleVertexElevation(World.World world, int wx, int wy)
    {
        float chunkPart = SampleChunkCornerBilerp(world, wx, wy);

        float sum = 0; int n = 0;
        for (int dy = -1; dy <= 0; dy++)
        for (int dx = -1; dx <= 0; dx++)
        {
            int cx = wx + dx, cy = wy + dy;
            var cc = World.World.ToChunkCoord(cx, cy);
            var ch = world.TryGetChunk(cc);
            if (ch is null) continue;
            var (lx, ly) = World.World.ToLocal(cx, cy);
            sum += ch.Elevation[lx, ly];
            n++;
        }
        float terrain = n == 0 ? 0f : sum / n;
        return chunkPart + terrain;
    }

    public static float SampleChunkCornerBilerp(World.World world, int wx, int wy)
    {
        int size = Chunk.Size;
        // Which chunk cell owns this vertex? Use floor division so negative
        // worlds behave.
        int cx = FloorDiv(wx, size);
        int cy = FloorDiv(wy, size);
        float u = (wx - cx * size) / (float)size;
        float v = (wy - cy * size) / (float)size;
        float c00 = world.GetChunkCornerElevation(cx,     cy);
        float c10 = world.GetChunkCornerElevation(cx + 1, cy);
        float c01 = world.GetChunkCornerElevation(cx,     cy + 1);
        float c11 = world.GetChunkCornerElevation(cx + 1, cy + 1);
        return (1 - u) * (1 - v) * c00
             + u       * (1 - v) * c10
             + (1 - u) * v       * c01
             + u       * v       * c11;
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a ^ b) < 0 && q * b != a) q--;
        return q;
    }

    private static Vector3 AverageCellColor(World.World world, int wx, int wy)
    {
        var acc = Vector3.Zero;
        int n = 0;
        for (int dy = -1; dy <= 0; dy++)
        for (int dx = -1; dx <= 0; dx++)
        {
            int cx = wx + dx, cy = wy + dy;
            var cc = World.World.ToChunkCoord(cx, cy);
            var ch = world.TryGetChunk(cc);
            if (ch is null) continue;
            var (lx, ly) = World.World.ToLocal(cx, cy);
            byte t = ch.TerrainType[lx, ly];
            if (t == (byte)Models.World.TerrainType.Unset) continue;  // wireframe, not fill
            acc += ColorForType(t);
            n++;
        }
        return n == 0 ? ColorForType(0) : acc / n;
    }

    public static Vector3 ColorForType(byte t) => t switch
    {
        1 => new Vector3(0.90f, 0.84f, 0.45f), // sand (yellow)
        2 => new Vector3(0.35f, 0.65f, 0.30f), // grass (green)
        3 => new Vector3(0.55f, 0.55f, 0.58f), // rock (grey)
        _ => Vector3.Zero,                     // unset: 0,0,0 — game reads this as no geometry
    };
}
