using System;
using System.Collections.Generic;
using System.Numerics;
using L22.Editor.Models.Rivers;
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
    // Phase 4 erosion depth (world units). Max amount a vertex gets pushed
    // down inside a river's bank — tapers linearly toward the edges.
    public const float RiverbedDepth = 0.9f;

    public static ChunkMeshData Build(World.World world, Chunk chunk,
                                       IReadOnlyList<River>? rivers = null)
    {
        int size = Chunk.Size;
        int vside = size + 1;             // 33x33 vertices
        int baseX = chunk.Coord.X * size;
        int baseY = chunk.Coord.Y * size;

        var positions = new Vector3[vside * vside];
        var normals = new Vector3[vside * vside];
        var riverInfluence = new float[vside * vside];

        // Sample each river's bezier curves once per chunk — gives the
        // erosion band a smooth polyline instead of straight segments
        // between control points. Shared with the normal pass below via
        // the local `sampled` list.
        List<List<BezierSampler.SampledPoint>>? sampled = null;
        if (rivers is { Count: > 0 })
        {
            sampled = new List<List<BezierSampler.SampledPoint>>();
            foreach (var r in rivers) sampled.AddRange(BezierSampler.SampleTree(r));
        }

        for (int vy = 0; vy < vside; vy++)
        for (int vx = 0; vx < vside; vx++)
        {
            int wx = baseX + vx;
            int wy = baseY + vy;
            var (jx, jz) = Jitter.Offset(wx, wy);
            float h = SampleVertexElevation(world, wx, wy) * ElevationScale;
            float wxWorld = wx + jx;
            float wzWorld = wy + jz;
            float influence = sampled is null ? 0f
                : BezierSampler.Influence(sampled, wxWorld, wzWorld);
            if (influence > 0f) h -= influence * RiverbedDepth;
            riverInfluence[vy * vside + vx] = influence;
            positions[vy * vside + vx] = new Vector3(wxWorld, h, wzWorld);
        }

        // Cross-chunk-aware normals: central-difference the elevation sampled
        // through the world (which reads neighbour chunks). Vertices on a
        // chunk's edge see the same gradient as the matching vertex in the
        // adjacent chunk, so lighting stays continuous across seams. Rivers
        // get folded in here too so the bank gradient shows as shading.
        for (int vy = 0; vy < vside; vy++)
        for (int vx = 0; vx < vside; vx++)
        {
            int wx = baseX + vx;
            int wy = baseY + vy;
            float hL = ErodedWorldHeight(world, wx - 1, wy, rivers);
            float hR = ErodedWorldHeight(world, wx + 1, wy, rivers);
            float hD = ErodedWorldHeight(world, wx, wy - 1, rivers);
            float hU = ErodedWorldHeight(world, wx, wy + 1, rivers);
            var n = new Vector3(hL - hR, 2f, hD - hU);
            normals[vy * vside + vx] = Vector3.Normalize(n);
        }

        // Per-vertex color: average of surrounding cells' terrain-type colors.
        // Vertices inside a river band blend toward a wet-mud tone so the
        // bank reads clearly even at low angles.
        var colors = new Vector3[vside * vside];
        var mud = new Vector3(0.32f, 0.26f, 0.20f);
        for (int vy = 0; vy < vside; vy++)
        for (int vx = 0; vx < vside; vx++)
        {
            int wx = baseX + vx;
            int wy = baseY + vy;
            var baseColor = AverageCellColor(world, wx, wy);
            float r = riverInfluence[vy * vside + vx];
            colors[vy * vside + vx] = r > 0f ? Vector3.Lerp(baseColor, mud, r) : baseColor;
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

    // World-space surface Y with any river erosion applied. Used by the
    // normal pass so bank slopes shade correctly. Walks straight segments
    // (not bezier) — called per-vertex and bezier sampling would quadruple
    // the normal pass cost. The visual gap is invisible once positions
    // themselves are bezier-shaped.
    private static float ErodedWorldHeight(World.World world, int wx, int wy,
                                            IReadOnlyList<River>? rivers)
    {
        float h = SampleVertexElevation(world, wx, wy) * ElevationScale;
        if (rivers is null) return h;
        var (jx, jz) = Jitter.Offset(wx, wy);
        float influence = ComputeRiverInfluence(rivers, wx + jx, wy + jz);
        return h - influence * RiverbedDepth;
    }

    // Max "inside-the-river" factor across all rivers at the given world
    // XZ. 0 = outside every river's band; 1 = exactly on a centreline;
    // tapers linearly toward the far bank. Branches recurse.
    public static float ComputeRiverInfluence(IReadOnlyList<River> rivers, float wx, float wz)
    {
        float best = 0f;
        foreach (var r in rivers)
        {
            float f = InfluenceFromRiver(r, wx, wz);
            if (f > best) best = f;
        }
        return best;
    }

    private static float InfluenceFromRiver(River r, float wx, float wz)
    {
        float best = 0f;
        for (int i = 0; i < r.Vertices.Count - 1; i++)
        {
            var a = r.Vertices[i];
            var b = r.Vertices[i + 1];
            float ax = a.WorldX, az = a.WorldZ;
            float bx = b.WorldX, bz = b.WorldZ;
            float dx = bx - ax, dz = bz - az;
            float len2 = dx * dx + dz * dz;
            float t = len2 > 1e-6f
                ? MathF.Max(0, MathF.Min(1, ((wx - ax) * dx + (wz - az) * dz) / len2))
                : 0f;
            float px = ax + dx * t;
            float pz = az + dz * t;
            float dxp = wx - px, dzp = wz - pz;
            float dist = MathF.Sqrt(dxp * dxp + dzp * dzp);
            float width = (1 - t) * a.WidthWorld + t * b.WidthWorld;
            float half = MathF.Max(0.01f, width * 0.5f);
            if (dist < half)
            {
                // Smoothstep the linear ramp so the bank reads as a cupped
                // channel instead of a straight-walled slot — flat near
                // the centreline, steeper at the bank, zero influence at
                // the edge. Keeps the same max depth without visible
                // stair-stepping on diagonal rivers.
                float linear = 1f - (dist / half);
                float influence = linear * linear * (3f - 2f * linear);
                if (influence > best) best = influence;
            }
        }
        foreach (var branch in r.Branches)
        {
            float f = InfluenceFromRiver(branch, wx, wz);
            if (f > best) best = f;
        }
        return best;
    }

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
