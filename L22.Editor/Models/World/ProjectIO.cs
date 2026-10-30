using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using L22.Editor.Models;
using L22.Editor.Models.Objects;

namespace L22.Editor.Models.World;

// Project format v3 — a folder, with per-chunk PNGs and an index.
//
//   scene-name.l22proj/
//     index.json               — version, kind, chunk list, chunk-corner
//                                heights. Sparse: only chunks that exist in
//                                the world appear here.
//     chunks/
//       <cx>_<cy>.elevation.png  — 32×32 RGBA. R=G=B=round(elevation × 255);
//                                  A always 255.
//       <cx>_<cy>.terrain.png    — 32×32 RGBA. Channel-weighted terrain:
//                                    (0,0,0) = Unset / no geometry
//                                    (255,0,0) = Sand
//                                    (0,255,0) = Grass
//                                    (0,0,255) = Rock
//
// Pixel (0,0) in each chunk PNG is local (lx=0, ly=0). Growing the world
// means adding new rows to index.chunks and dropping two more PNGs in
// chunks/ — no atlas repack.
//
// Legacy loaders:
//   - A single file (not a folder) is treated as the v1 gzip-binary save
//     (scene-2 and earlier still open).
public static class ProjectIO
{
    private const int CurrentVersion = 3;
    private const int LegacyBinaryVersion = 1;
    private static readonly byte[] LegacyMagic = Encoding.ASCII.GetBytes("L22PROJ\0");

    public sealed class LoadResult
    {
        public World World { get; init; } = null!;
        public DocumentKind Kind { get; init; }
        public List<ObjectInstance> Objects { get; init; } = new();
    }

    private sealed class Index
    {
        [JsonPropertyName("version")]      public int Version { get; set; } = CurrentVersion;
        [JsonPropertyName("kind")]         public string Kind { get; set; } = "Scene";
        [JsonPropertyName("chunkSize")]    public int ChunkSize { get; set; } = Chunk.Size;
        [JsonPropertyName("chunks")]       public List<ChunkEntry> Chunks { get; set; } = new();
        [JsonPropertyName("chunkCorners")] public List<CornerEntry> ChunkCorners { get; set; } = new();
    }
    private sealed class ChunkEntry { [JsonPropertyName("cx")] public int Cx { get; set; } [JsonPropertyName("cy")] public int Cy { get; set; } }
    private sealed class CornerEntry { [JsonPropertyName("cx")] public int Cx { get; set; } [JsonPropertyName("cy")] public int Cy { get; set; } [JsonPropertyName("value")] public float Value { get; set; } }

    public static void Save(World world, DocumentKind kind, string folderPath,
                            IReadOnlyList<ObjectInstance>? objects = null)
    {
        // Save-As can target an existing file (old binary) — convert it to
        // a folder, overwriting the file in place.
        if (File.Exists(folderPath)) File.Delete(folderPath);
        Directory.CreateDirectory(folderPath);
        var chunksDir = Path.Combine(folderPath, "chunks");
        Directory.CreateDirectory(chunksDir);

        var index = new Index
        {
            Version = CurrentVersion,
            Kind = kind.ToString(),
            ChunkSize = Chunk.Size,
            Chunks = new List<ChunkEntry>(world.Chunks.Count),
            ChunkCorners = BuildCornerList(world),
        };

        foreach (var kv in world.Chunks)
        {
            var ch = kv.Value;
            index.Chunks.Add(new ChunkEntry { Cx = ch.Coord.X, Cy = ch.Coord.Y });

            WriteChunkPngs(chunksDir, ch);
        }

        // Prune stale per-chunk PNGs left over from a previous save (chunks
        // the user has since removed). Any PNG not matching an index entry
        // goes.
        var kept = new HashSet<string>(index.Chunks.Count * 2);
        foreach (var e in index.Chunks)
        {
            kept.Add(ChunkPngName(e.Cx, e.Cy, "elevation"));
            kept.Add(ChunkPngName(e.Cx, e.Cy, "terrain"));
        }
        foreach (var file in Directory.EnumerateFiles(chunksDir, "*.png"))
            if (!kept.Contains(Path.GetFileName(file))) File.Delete(file);

        File.WriteAllText(Path.Combine(folderPath, "index.json"),
            JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));

        // Always write objects.json so a reader doesn't guess at absence vs
        // never-written. Empty list is written as {"version":1,"instances":[]}.
        ObjectIO.SaveInstances(folderPath, objects ?? Array.Empty<ObjectInstance>());
    }

    public static LoadResult Load(string path, string fileId)
    {
        if (Directory.Exists(path)) return LoadFolder(path, fileId);
        if (File.Exists(path))      return LoadLegacyBinary(path, fileId);
        throw new FileNotFoundException($"project path not found: {path}");
    }

    private static LoadResult LoadFolder(string folderPath, string fileId)
    {
        var indexPath = Path.Combine(folderPath, "index.json");
        if (!File.Exists(indexPath))
            throw new InvalidDataException($"missing index.json in {folderPath}");

        var index = JsonSerializer.Deserialize<Index>(File.ReadAllText(indexPath))
            ?? throw new InvalidDataException("index.json is empty/invalid");
        if (index.Version > CurrentVersion)
            throw new InvalidDataException($"unsupported folder version {index.Version} (editor supports up to {CurrentVersion})");
        if (index.ChunkSize != Chunk.Size)
            throw new InvalidDataException($"chunkSize mismatch: file={index.ChunkSize}, editor={Chunk.Size}");

        var chunksDir = Path.Combine(folderPath, "chunks");
        var world = new World(fileId);
        foreach (var e in index.Chunks)
        {
            var ch = world.GetOrCreateChunk(new ChunkCoord(e.Cx, e.Cy));
            ReadChunkPngs(chunksDir, ch);
            ch.MeshDirty = true;
        }

        foreach (var c in index.ChunkCorners)
            world.SetChunkCornerElevation(c.Cx, c.Cy, c.Value);

        var kind = Enum.TryParse<DocumentKind>(index.Kind, ignoreCase: true, out var k)
            ? k : DocumentKind.Scene;
        var objects = ObjectIO.LoadInstances(folderPath);
        return new LoadResult { World = world, Kind = kind, Objects = objects };
    }

    private static void WriteChunkPngs(string chunksDir, Chunk ch)
    {
        int size = Chunk.Size;
        var elev = new byte[size * size * 4];
        var terr = new byte[size * size * 4];
        for (int ly = 0; ly < size; ly++)
        for (int lx = 0; lx < size; lx++)
        {
            int idx = (ly * size + lx) * 4;

            byte eByte = (byte)Math.Clamp((int)MathF.Round(ch.Elevation[lx, ly] * 255f), 0, 255);
            elev[idx + 0] = eByte;
            elev[idx + 1] = eByte;
            elev[idx + 2] = eByte;
            elev[idx + 3] = 255;

            byte t = ch.TerrainType[lx, ly];
            terr[idx + 0] = t == (byte)TerrainType.Sand  ? (byte)255 : (byte)0;
            terr[idx + 1] = t == (byte)TerrainType.Grass ? (byte)255 : (byte)0;
            terr[idx + 2] = t == (byte)TerrainType.Rock  ? (byte)255 : (byte)0;
            terr[idx + 3] = 255;
        }
        PngWriter.WriteRgba(Path.Combine(chunksDir, ChunkPngName(ch.Coord.X, ch.Coord.Y, "elevation")),
                             elev, size, size, flipY: false);
        PngWriter.WriteRgba(Path.Combine(chunksDir, ChunkPngName(ch.Coord.X, ch.Coord.Y, "terrain")),
                             terr, size, size, flipY: false);
    }

    private static void ReadChunkPngs(string chunksDir, Chunk ch)
    {
        int size = Chunk.Size;
        var elevPath = Path.Combine(chunksDir, ChunkPngName(ch.Coord.X, ch.Coord.Y, "elevation"));
        var terrPath = Path.Combine(chunksDir, ChunkPngName(ch.Coord.X, ch.Coord.Y, "terrain"));
        var (ePx, ew, eh) = PngReader.ReadRgba(elevPath);
        var (tPx, tw, th) = PngReader.ReadRgba(terrPath);
        if (ew != size || eh != size || tw != size || th != size)
            throw new InvalidDataException(
                $"chunk ({ch.Coord.X},{ch.Coord.Y}) PNG dimensions wrong: "
                + $"elevation {ew}x{eh}, terrain {tw}x{th}, expected {size}x{size}");

        for (int ly = 0; ly < size; ly++)
        for (int lx = 0; lx < size; lx++)
        {
            int idx = (ly * size + lx) * 4;
            byte eByte = ePx[idx];
            byte tR = tPx[idx + 0];
            byte tG = tPx[idx + 1];
            byte tB = tPx[idx + 2];

            ch.Elevation[lx, ly] = eByte / 255f;

            byte type = (byte)TerrainType.Unset;
            if (tR > 0 || tG > 0 || tB > 0)
            {
                type = tR >= tG && tR >= tB ? (byte)TerrainType.Sand
                     : tG >= tB             ? (byte)TerrainType.Grass
                     :                        (byte)TerrainType.Rock;
            }
            ch.TerrainType[lx, ly] = type;
        }
    }

    private static string ChunkPngName(int cx, int cy, string layer)
        => $"{cx}_{cy}.{layer}.png";

    private static List<CornerEntry> BuildCornerList(World world)
    {
        var list = new List<CornerEntry>();
        foreach (var (cx, cy) in world.EnumerateSetChunkCorners())
            list.Add(new CornerEntry { Cx = cx, Cy = cy, Value = world.GetChunkCornerElevation(cx, cy) });
        return list;
    }

    // ---- Legacy binary (v1) loader -----------------------------------------

    private static LoadResult LoadLegacyBinary(string path, string fileId)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(gz, Encoding.UTF8, leaveOpen: false);

        var magic = r.ReadBytes(LegacyMagic.Length);
        if (magic.Length != LegacyMagic.Length)
            throw new InvalidDataException($"not a legacy .l22proj file (truncated magic): {path}");
        for (int i = 0; i < LegacyMagic.Length; i++)
            if (magic[i] != LegacyMagic[i])
                throw new InvalidDataException($"unknown file format: {path}");

        ushort version = r.ReadUInt16();
        if (version > LegacyBinaryVersion)
            throw new InvalidDataException($"unsupported legacy version {version}");

        var kind = (DocumentKind)r.ReadByte();
        r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32();  // reserved

        var world = new World(fileId);
        int chunkCount = r.ReadInt32();
        for (int i = 0; i < chunkCount; i++)
        {
            int cx = r.ReadInt32();
            int cy = r.ReadInt32();
            var ch = world.GetOrCreateChunk(new ChunkCoord(cx, cy));
            for (int ly = 0; ly < Chunk.Size; ly++)
                for (int lx = 0; lx < Chunk.Size; lx++)
                    ch.TerrainType[lx, ly] = r.ReadByte();
            for (int ly = 0; ly < Chunk.Size; ly++)
                for (int lx = 0; lx < Chunk.Size; lx++)
                    ch.Elevation[lx, ly] = r.ReadSingle();
            ch.MeshDirty = true;
        }

        while (true)
        {
            var tagBytes = r.ReadBytes(4);
            if (tagBytes.Length < 4) break;
            string tag = Encoding.ASCII.GetString(tagBytes);
            int len = r.ReadInt32();
            if (tag == "CCVL")
            {
                int count = r.ReadInt32();
                int consumed = 4;
                int entryBytes = Math.Min(count * 12, Math.Max(0, len - consumed));
                int entries = entryBytes / 12;
                for (int i = 0; i < entries; i++)
                {
                    int cx = r.ReadInt32();
                    int cy = r.ReadInt32();
                    float v = r.ReadSingle();
                    world.SetChunkCornerElevation(cx, cy, v);
                    consumed += 12;
                }
                int leftover = len - consumed;
                SkipBytes(r, leftover);
            }
            else
            {
                SkipBytes(r, len);
            }
        }

        return new LoadResult { World = world, Kind = kind };
    }

    private static void SkipBytes(BinaryReader r, int count)
    {
        if (count <= 0) return;
        var buf = new byte[Math.Min(4096, count)];
        int remaining = count;
        while (remaining > 0)
        {
            int take = Math.Min(buf.Length, remaining);
            int read = r.Read(buf, 0, take);
            if (read == 0) return;
            remaining -= read;
        }
    }
}
