using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using L22.Editor.Models;

namespace L22.Editor.Models.World;

// Binary save/load for .l22proj files.
//
// Container is gzip. Inner stream:
//   Magic         : 8 bytes "L22PROJ\0"
//   Version       : uint16 LE
//   DocKind       : byte  (0 = Scene, 1 = Prefab)
//   Reserved      : 4 × int32 (bounds placeholders, zero today)
//   ChunkCount    : int32
//   For each chunk:
//     cx, cy      : int32, int32
//     TerrainType : byte[Chunk.Size * Chunk.Size]  (row-major: ly*Size + lx)
//     Elevation   : float[Chunk.Size * Chunk.Size] (same ordering)
//   Named sections (0..N, until EOF):
//     tag    : 4 bytes ASCII
//     length : int32 (payload byte count)
//     payload: bytes
//
// Known section tags:
//   "CCVL" — chunk-corner elevation.
//              count : int32
//              entries : (int32 cx, int32 cy, float value) × count
// Unknown tags are skipped so new sections can be added without breaking
// older savefiles.
public static class ProjectIO
{
    private const int CurrentVersion = 1;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("L22PROJ\0");

    public sealed class LoadResult
    {
        public World World { get; init; } = null!;
        public DocumentKind Kind { get; init; }
    }

    public static void Save(World world, DocumentKind kind, string path)
    {
        // Write to a sibling .tmp then atomic-replace. A crash mid-write
        // leaves the previous saved file intact instead of a truncated one.
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
        using (var w = new BinaryWriter(gz, Encoding.UTF8, leaveOpen: false))
        {
            w.Write(Magic);
            w.Write((ushort)CurrentVersion);
            w.Write((byte)kind);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);  // reserved bounds

            var chunks = world.Chunks;
            w.Write(chunks.Count);
            foreach (var kv in chunks)
            {
                var c = kv.Value;
                w.Write(c.Coord.X);
                w.Write(c.Coord.Y);
                WriteChunkCells(w, c);
            }

            WriteCornerSection(w, world);
        }

        if (File.Exists(path)) File.Replace(tmp, path, destinationBackupFileName: null);
        else File.Move(tmp, path);
    }

    public static LoadResult Load(string path, string fileId)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(gz, Encoding.UTF8, leaveOpen: false);

        var magic = r.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length)
            throw new InvalidDataException("Not a .l22proj file (truncated magic).");
        for (int i = 0; i < Magic.Length; i++)
            if (magic[i] != Magic[i])
                throw new InvalidDataException("Not a .l22proj file (magic mismatch).");

        ushort version = r.ReadUInt16();
        if (version > CurrentVersion)
            throw new InvalidDataException($"Unsupported .l22proj version {version} (editor supports up to {CurrentVersion}).");

        var kind = (DocumentKind)r.ReadByte();
        r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32();  // reserved bounds

        var world = new World(fileId);
        int chunkCount = r.ReadInt32();
        for (int i = 0; i < chunkCount; i++)
        {
            int cx = r.ReadInt32();
            int cy = r.ReadInt32();
            var ch = world.GetOrCreateChunk(new ChunkCoord(cx, cy));
            ReadChunkCells(r, ch);
            ch.MeshDirty = true;
        }

        // Named sections — stop when the stream runs out mid-header.
        while (true)
        {
            var tag = ReadTagOrEof(r);
            if (tag is null) break;
            int len = r.ReadInt32();
            if (tag == "CCVL") ReadCornerSection(r, world, len);
            else SkipBytes(r, len);
        }

        return new LoadResult { World = world, Kind = kind };
    }

    private static void WriteChunkCells(BinaryWriter w, Chunk c)
    {
        int size = Chunk.Size;
        for (int ly = 0; ly < size; ly++)
            for (int lx = 0; lx < size; lx++)
                w.Write(c.TerrainType[lx, ly]);
        for (int ly = 0; ly < size; ly++)
            for (int lx = 0; lx < size; lx++)
                w.Write(c.Elevation[lx, ly]);
    }

    private static void ReadChunkCells(BinaryReader r, Chunk c)
    {
        int size = Chunk.Size;
        for (int ly = 0; ly < size; ly++)
            for (int lx = 0; lx < size; lx++)
                c.TerrainType[lx, ly] = r.ReadByte();
        for (int ly = 0; ly < size; ly++)
            for (int lx = 0; lx < size; lx++)
                c.Elevation[lx, ly] = r.ReadSingle();
    }

    private static void WriteCornerSection(BinaryWriter w, World world)
    {
        var corners = new List<(int cx, int cy, float v)>();
        foreach (var (cx, cy) in world.EnumerateSetChunkCorners())
            corners.Add((cx, cy, world.GetChunkCornerElevation(cx, cy)));
        if (corners.Count == 0) return;

        w.Write(Encoding.ASCII.GetBytes("CCVL"));
        int payloadBytes = 4 + corners.Count * 12;
        w.Write(payloadBytes);
        w.Write(corners.Count);
        foreach (var e in corners)
        {
            w.Write(e.cx);
            w.Write(e.cy);
            w.Write(e.v);
        }
    }

    private static void ReadCornerSection(BinaryReader r, World world, int length)
    {
        // Honor the length field so a future CCVL extension (extra trailing
        // fields) stays stream-aligned for old readers. We consume exactly
        // `length` bytes, parse what we understand (count + 12-byte entries),
        // skip any trailing bytes.
        int count = r.ReadInt32();
        int consumed = 4;
        int entryBytes = Math.Min(count * 12, Math.Max(0, length - consumed));
        int fullEntries = entryBytes / 12;
        for (int i = 0; i < fullEntries; i++)
        {
            int cx = r.ReadInt32();
            int cy = r.ReadInt32();
            float v = r.ReadSingle();
            world.SetChunkCornerElevation(cx, cy, v);
            consumed += 12;
        }
        int remaining = length - consumed;
        if (remaining > 0) SkipBytes(r, remaining);
    }

    private static string? ReadTagOrEof(BinaryReader r)
    {
        var buf = r.ReadBytes(4);
        if (buf.Length < 4) return null;
        return Encoding.ASCII.GetString(buf);
    }

    private static void SkipBytes(BinaryReader r, int count)
    {
        // GZipStream doesn't support Seek, so drain.
        var buf = new byte[Math.Min(4096, count)];
        int remaining = count;
        while (remaining > 0)
        {
            int take = Math.Min(buf.Length, remaining);
            int read = r.Read(buf, 0, take);
            if (read == 0) throw new EndOfStreamException("Section payload truncated.");
            remaining -= read;
        }
    }
}
