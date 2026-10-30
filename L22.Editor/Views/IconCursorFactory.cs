using System;
using System.IO;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace L22.Editor.Views;

public static class IconCursorFactory
{
    public static string? DebugDumpDir { get; set; }

    public static Cursor FromGeometry(Geometry geometry, PixelPoint hotspot, int size = 32,
                                       Color? fill = null, Color? stroke = null,
                                       string? debugName = null)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        var fillBrush = new SolidColorBrush(fill ?? Colors.White);
        var strokePen = new Pen(new SolidColorBrush(stroke ?? Color.FromArgb(255, 20, 20, 20)), 1.5);

        using (var ctx = rtb.CreateDrawingContext())
        {
            // Clear to transparent — RTB may contain garbage pixels on some
            // backends before anything is drawn to it.
            ctx.FillRectangle(Brushes.Transparent, new Rect(0, 0, size, size));
            using (ctx.PushTransform(Matrix.CreateScale(size / 24.0, size / 24.0)))
            {
                ctx.DrawGeometry(fillBrush, strokePen, geometry);
            }
        }

        // Round-trip through PNG so we hand Win32's cursor factory a plain
        // decoded Bitmap, not a GPU-backed RenderTargetBitmap.
        using var ms = new MemoryStream();
        rtb.Save(ms);

        if (DebugDumpDir is not null && debugName is not null)
        {
            try
            {
                Directory.CreateDirectory(DebugDumpDir);
                File.WriteAllBytes(Path.Combine(DebugDumpDir, $"cursor_{debugName}.png"), ms.ToArray());
            }
            catch { /* best-effort */ }
        }

        ms.Position = 0;
        var decoded = new Bitmap(ms);
        return new Cursor(decoded, hotspot);
    }
}
