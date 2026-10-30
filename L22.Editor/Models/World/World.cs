using System;
using System.Collections.Generic;

namespace L22.Editor.Models.World;

public sealed class World
{
    private readonly Dictionary<ChunkCoord, Chunk> _chunks = new();

    // Elevation at the 4 corners of a chunk (shared with up to 3 adjacent
    // chunks). Keyed by corner-grid coord: corner (cx, cy) lives at world
    // position (cx * Chunk.Size, cy * Chunk.Size). Default is 0 — only
    // explicitly set corners live in the dict.
    private readonly Dictionary<(int cx, int cy), float> _chunkCornerElev = new();

    public string FileId { get; }

    public World(string fileId)
    {
        FileId = fileId;
    }

    public IReadOnlyDictionary<ChunkCoord, Chunk> Chunks => _chunks;

    public event Action<Chunk>? ChunkChanged;
    public event Action<Chunk>? ChunkCreated;

    public static ChunkCoord ToChunkCoord(int worldX, int worldY) =>
        new(FloorDiv(worldX, Chunk.Size), FloorDiv(worldY, Chunk.Size));

    public static (int lx, int ly) ToLocal(int worldX, int worldY) =>
        (FloorMod(worldX, Chunk.Size), FloorMod(worldY, Chunk.Size));

    public Chunk? TryGetChunk(ChunkCoord c) =>
        _chunks.TryGetValue(c, out var ch) ? ch : null;

    public Chunk GetOrCreateChunk(ChunkCoord c)
    {
        if (_chunks.TryGetValue(c, out var ch)) return ch;
        ch = new Chunk(c);
        _chunks[c] = ch;
        ChunkCreated?.Invoke(ch);

        // Existing neighbors previously computed their edge vertices assuming
        // this chunk didn't exist. Dirty them so they re-sample.
        MarkDirty(new ChunkCoord(c.X - 1, c.Y));
        MarkDirty(new ChunkCoord(c.X + 1, c.Y));
        MarkDirty(new ChunkCoord(c.X, c.Y - 1));
        MarkDirty(new ChunkCoord(c.X, c.Y + 1));
        return ch;
    }

    public byte GetTerrainType(int worldX, int worldY)
    {
        var cc = ToChunkCoord(worldX, worldY);
        var ch = TryGetChunk(cc);
        if (ch is null) return (byte)Models.World.TerrainType.Unset;
        var (lx, ly) = ToLocal(worldX, worldY);
        return ch.TerrainType[lx, ly];
    }

    public void SetTerrainType(int worldX, int worldY, byte type)
    {
        var cc = ToChunkCoord(worldX, worldY);
        var ch = GetOrCreateChunk(cc);
        var (lx, ly) = ToLocal(worldX, worldY);
        if (ch.TerrainType[lx, ly] == type) return;
        ch.TerrainType[lx, ly] = type;
        ch.MeshDirty = true;
        ChunkChanged?.Invoke(ch);
        MarkNeighborsIfOnEdge(cc, lx, ly);
    }

    public float GetElevation(int worldX, int worldY)
    {
        var cc = ToChunkCoord(worldX, worldY);
        var ch = TryGetChunk(cc);
        if (ch is null) return 0f;
        var (lx, ly) = ToLocal(worldX, worldY);
        return ch.Elevation[lx, ly];
    }

    // Chunk-corner elevation API. (cx, cy) is a corner-grid coord; the
    // corner sits at world position (cx * Chunk.Size, cy * Chunk.Size) and
    // is shared by up to 4 chunks: (cx-1, cy-1), (cx, cy-1), (cx-1, cy),
    // (cx, cy). Setting it marks all touching chunks dirty.
    public float GetChunkCornerElevation(int cx, int cy)
        => _chunkCornerElev.TryGetValue((cx, cy), out var v) ? v : 0f;

    public bool HasChunkCornerElevation(int cx, int cy) => _chunkCornerElev.ContainsKey((cx, cy));

    // Corners the user has explicitly pinned a height on. Different from
    // EnumerateChunkCorners (which lists every corner touched by any chunk).
    public IEnumerable<(int cx, int cy)> EnumerateSetChunkCorners() => _chunkCornerElev.Keys;

    public void SetChunkCornerElevation(int cx, int cy, float value)
    {
        _chunkCornerElev[(cx, cy)] = value;
        MarkCornerNeighborsDirty(cx, cy);
    }

    public void ClearChunkCornerElevation(int cx, int cy)
    {
        if (_chunkCornerElev.Remove((cx, cy))) MarkCornerNeighborsDirty(cx, cy);
    }

    // Overall elevation extent across every materialised chunk, in painted
    // (0..1) units — chunk-corner delta plus the biggest/smallest cell
    // value. The shader uses this to pick a greyscale range that scales as
    // the world grows beyond the default [0, 1] bracket.
    public (float low, float high) GetElevationRangeNormalized()
    {
        float minCell = 0f, maxCell = 0f;
        foreach (var ch in _chunks.Values)
        {
            for (int ly = 0; ly < Chunk.Size; ly++)
                for (int lx = 0; lx < Chunk.Size; lx++)
                {
                    var e = ch.Elevation[lx, ly];
                    if (e < minCell) minCell = e;
                    if (e > maxCell) maxCell = e;
                }
        }
        float minCorner = 0f, maxCorner = 0f;
        foreach (var v in _chunkCornerElev.Values)
        {
            if (v < minCorner) minCorner = v;
            if (v > maxCorner) maxCorner = v;
        }
        return (minCell + minCorner, maxCell + maxCorner);
    }

    public IEnumerable<(int cx, int cy)> EnumerateChunkCorners()
    {
        var seen = new HashSet<(int, int)>();
        foreach (var c in _chunks.Keys)
        {
            foreach (var cc in new (int, int)[] { (c.X, c.Y), (c.X + 1, c.Y), (c.X, c.Y + 1), (c.X + 1, c.Y + 1) })
                if (seen.Add(cc)) yield return cc;
        }
    }

    private void MarkCornerNeighborsDirty(int cx, int cy)
    {
        for (int dy = -1; dy <= 0; dy++)
        for (int dx = -1; dx <= 0; dx++)
        {
            var cc = new ChunkCoord(cx + dx, cy + dy);
            if (_chunks.TryGetValue(cc, out var ch))
            {
                ch.MeshDirty = true;
                ChunkChanged?.Invoke(ch);
            }
        }
    }

    public void SetElevation(int worldX, int worldY, float value)
    {
        var cc = ToChunkCoord(worldX, worldY);
        var ch = GetOrCreateChunk(cc);
        var (lx, ly) = ToLocal(worldX, worldY);
        if (ch.Elevation[lx, ly] == value) return;
        ch.Elevation[lx, ly] = value;
        ch.MeshDirty = true;
        ChunkChanged?.Invoke(ch);
        MarkNeighborsIfOnEdge(cc, lx, ly);
    }

    // Vertex positions at chunk borders are shared with neighbors; mark them
    // dirty so seams don't stay stale.
    private void MarkNeighborsIfOnEdge(ChunkCoord cc, int lx, int ly)
    {
        if (lx == 0) MarkDirty(new ChunkCoord(cc.X - 1, cc.Y));
        if (ly == 0) MarkDirty(new ChunkCoord(cc.X, cc.Y - 1));
        if (lx == Chunk.Size - 1) MarkDirty(new ChunkCoord(cc.X + 1, cc.Y));
        if (ly == Chunk.Size - 1) MarkDirty(new ChunkCoord(cc.X, cc.Y + 1));
    }

    private void MarkDirty(ChunkCoord cc)
    {
        if (_chunks.TryGetValue(cc, out var ch))
        {
            ch.MeshDirty = true;
            ChunkChanged?.Invoke(ch);
        }
    }

    public void InitializeDefault(int chunksX, int chunksY)
    {
        for (int cx = 0; cx < chunksX; cx++)
            for (int cy = 0; cy < chunksY; cy++)
                GetOrCreateChunk(new ChunkCoord(cx, cy));
    }

    // Paint every cell of every existing chunk with the given terrain type.
    // Used to give a new document a visible starting surface so the user
    // doesn't see a dark empty plane. Not a command — this is initialisation
    // state, not an undoable edit.
    public void FillAllCellsWithType(byte type)
    {
        foreach (var ch in _chunks.Values)
        {
            for (int ly = 0; ly < Chunk.Size; ly++)
                for (int lx = 0; lx < Chunk.Size; lx++)
                    ch.TerrainType[lx, ly] = type;
            ch.MeshDirty = true;
        }
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a ^ b) < 0 && q * b != a) q--;
        return q;
    }

    private static int FloorMod(int a, int b)
    {
        int r = a % b;
        if (r < 0) r += b;
        return r;
    }
}
