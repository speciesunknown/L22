using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace L22.Editor;

// Minimal, dependency-free PNG writer. Writes an 8-bit RGBA PNG with filter=0
// rows and zlib-compressed IDAT. Flips Y when flipY=true (GL framebuffers are
// bottom-up, PNG is top-down).
public static class PngWriter
{
    public static void WriteRgba(string path, byte[] rgba, int width, int height, bool flipY)
    {
        using var fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, width);
        WriteBE(ihdr, 4, height);
        ihdr[8] = 8;    // bit depth per channel
        ihdr[9] = 6;    // color type: RGBA
        ihdr[10] = 0;   // deflate
        ihdr[11] = 0;   // no row filter smarts (we emit filter=None each row)
        ihdr[12] = 0;   // not interlaced
        WriteChunk(fs, "IHDR", ihdr);

        int stride = width * 4;
        var raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int srcY = flipY ? height - 1 - y : y;
            raw[y * (stride + 1)] = 0;
            Buffer.BlockCopy(rgba, srcY * stride, raw, y * (stride + 1) + 1, stride);
        }

        using var ms = new MemoryStream();
        using (var zs = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            zs.Write(raw, 0, raw.Length);
        WriteChunk(fs, "IDAT", ms.ToArray());

        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var len = new byte[4]; WriteBE(len, 0, data.Length);
        s.Write(len);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(typeBytes, data);
        var crcBytes = new byte[4]; WriteBE(crcBytes, 0, (int)crc);
        s.Write(crcBytes);
    }

    private static void WriteBE(byte[] a, int o, int v)
    {
        a[o] = (byte)(v >> 24);
        a[o + 1] = (byte)(v >> 16);
        a[o + 2] = (byte)(v >> 8);
        a[o + 3] = (byte)v;
    }

    private static uint Crc32(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFFu;
        for (int i = 0; i < a.Length; i++) c = Byte(c, a[i]);
        for (int i = 0; i < b.Length; i++) c = Byte(c, b[i]);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint Byte(uint c, byte b)
    {
        c ^= b;
        for (int i = 0; i < 8; i++)
            c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
        return c;
    }
}
