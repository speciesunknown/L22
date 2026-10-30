using System;
using System.IO;
using System.IO.Compression;

namespace L22.Editor;

// Minimal 8-bit RGBA PNG reader. Mirror of PngWriter. Handles the five
// per-row filter types (None/Sub/Up/Average/Paeth), rejects anything that
// isn't non-interlaced 8-bit RGBA. zlib via System.IO.Compression.ZLibStream.
public static class PngReader
{
    public static (byte[] rgba, int width, int height) ReadRgba(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);

        Span<byte> sig = stackalloc byte[8];
        if (br.Read(sig) != 8 || sig[0] != 0x89 || sig[1] != 0x50 || sig[2] != 0x4E
            || sig[3] != 0x47 || sig[4] != 0x0D || sig[5] != 0x0A
            || sig[6] != 0x1A || sig[7] != 0x0A)
            throw new InvalidDataException($"not a PNG file: {path}");

        int width = 0, height = 0;
        using var idat = new MemoryStream();

        while (fs.Position < fs.Length)
        {
            int len = ReadInt32BE(br);
            var type = br.ReadBytes(4);
            string tag = System.Text.Encoding.ASCII.GetString(type);
            var data = br.ReadBytes(len);
            _ = br.ReadBytes(4);  // CRC — not verified

            if (tag == "IHDR")
            {
                width = BeInt(data, 0);
                height = BeInt(data, 4);
                byte depth = data[8];
                byte colorType = data[9];
                byte compression = data[10];
                byte filter = data[11];
                byte interlace = data[12];
                if (depth != 8 || colorType != 6 || compression != 0 || filter != 0 || interlace != 0)
                    throw new InvalidDataException(
                        $"unsupported PNG: depth={depth}, colorType={colorType}, "
                        + $"compression={compression}, filter={filter}, interlace={interlace}");
            }
            else if (tag == "IDAT")
            {
                idat.Write(data, 0, data.Length);
            }
            else if (tag == "IEND")
            {
                break;
            }
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);

        int bytesPerPixel = 4;
        int stride = width * bytesPerPixel;
        var rgba = new byte[width * height * bytesPerPixel];
        var prevRow = new byte[stride];
        var curRow = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            int filterType = zlib.ReadByte();
            if (filterType < 0) throw new EndOfStreamException("PNG IDAT truncated");
            ReadExact(zlib, curRow, 0, stride);

            // Reverse filter: filtered pixels in-place → unfiltered.
            for (int x = 0; x < stride; x++)
            {
                byte left  = x >= bytesPerPixel ? curRow[x - bytesPerPixel] : (byte)0;
                byte up    = prevRow[x];
                byte upleft = x >= bytesPerPixel ? prevRow[x - bytesPerPixel] : (byte)0;
                int r = filterType switch
                {
                    0 => curRow[x],
                    1 => (curRow[x] + left) & 0xFF,
                    2 => (curRow[x] + up) & 0xFF,
                    3 => (curRow[x] + (left + up) / 2) & 0xFF,
                    4 => (curRow[x] + Paeth(left, up, upleft)) & 0xFF,
                    _ => throw new InvalidDataException($"unknown PNG filter {filterType}"),
                };
                curRow[x] = (byte)r;
            }

            Buffer.BlockCopy(curRow, 0, rgba, y * stride, stride);
            (prevRow, curRow) = (curRow, prevRow);
        }

        return (rgba, width, height);
    }

    private static int Paeth(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    private static int BeInt(byte[] b, int o)
        => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    private static int ReadInt32BE(BinaryReader br)
    {
        var b = br.ReadBytes(4);
        return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
    }

    private static void ReadExact(Stream s, byte[] buf, int off, int count)
    {
        while (count > 0)
        {
            int n = s.Read(buf, off, count);
            if (n == 0) throw new EndOfStreamException("PNG IDAT ran out mid-row");
            off += n;
            count -= n;
        }
    }
}
