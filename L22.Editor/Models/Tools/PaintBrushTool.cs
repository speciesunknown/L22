using System;
using L22.Editor.Models.Commands;
using L22.Editor.Models.World;
using L22.Editor.ViewModels;

namespace L22.Editor.Models.Tools;

public sealed class PaintBrushTool : ITool
{
    private readonly World.World _world;
    private readonly CommandHistory _history;
    private readonly BrushViewModel _brush;

    private TerrainTypePaintCommand? _typeCmd;
    private ElevationPaintCommand? _elevCmd;

    public PaintBrushTool(World.World world, CommandHistory history, BrushViewModel brush)
    {
        _world = world;
        _history = history;
        _brush = brush;
    }

    public string Name => "Paint Brush";

    public void BeginStroke(int wx, int wy)
    {
        if (_brush.ActiveLayer == LayerKind.TerrainType)
            _typeCmd = new TerrainTypePaintCommand();
        else
            _elevCmd = new ElevationPaintCommand();
        Stamp(wx, wy);
    }

    public void ContinueStroke(int wx, int wy) => Stamp(wx, wy);

    public void EndStroke()
    {
        if (_typeCmd is { Count: > 0 }) _history.PushExecuted(_typeCmd);
        if (_elevCmd is { Count: > 0 }) _history.PushExecuted(_elevCmd);
        _typeCmd = null;
        _elevCmd = null;
    }

    private void Stamp(int cx, int cy)
    {
        int r = Math.Max(1, _brush.Radius);
        int r2 = r * r;
        float strength = Math.Clamp(_brush.Strength, 0f, 1f);
        var mode = _brush.SculptMode;

        for (int dy = -r + 1; dy < r; dy++)
        for (int dx = -r + 1; dx < r; dx++)
        {
            int d2 = dx * dx + dy * dy;
            if (d2 >= r2) continue;
            int x = cx + dx, y = cy + dy;

            float t = 1f - MathF.Sqrt(d2) / r;
            float w = MathF.Pow(t, 1.5f) * strength;

            if (_brush.ActiveLayer == LayerKind.TerrainType && _typeCmd is not null)
            {
                // Terrain-type doesn't support smoothing; alt/shift-alt are
                // noops. Shift inverts to "erase to Unset" so the game reads
                // the cell as missing geometry.
                byte paintType = mode == SculptMode.Subtractive
                    ? (byte)Models.World.TerrainType.Unset
                    : _brush.TerrainType;
                if (mode == SculptMode.Smooth || mode == SculptMode.Unsmooth) continue;
                if (w >= 0.5f || (w > 0f && _brush.PaintSolid))
                    _typeCmd.Record(_world, x, y, paintType);
            }
            else if (_brush.ActiveLayer == LayerKind.Elevation && _elevCmd is not null)
            {
                float cur = _world.GetElevation(x, y);
                float next = cur;
                switch (mode)
                {
                    case SculptMode.Additive:
                        next = cur + w;
                        break;
                    case SculptMode.Subtractive:
                        next = cur - w;
                        break;
                    case SculptMode.Smooth:
                    {
                        float avg = NeighborElevationAverage(x, y);
                        next = cur + (avg - cur) * w;
                        break;
                    }
                    case SculptMode.Unsmooth:
                    {
                        float avg = NeighborElevationAverage(x, y);
                        next = cur + (cur - avg) * w;
                        break;
                    }
                }
                next = Math.Clamp(next, 0f, 1f);
                _elevCmd.Record(_world, x, y, next);
            }
        }
    }

    private float NeighborElevationAverage(int x, int y)
    {
        float sum = 0f; int n = 0;
        for (int dy = -1; dy <= 1; dy++)
        for (int dx = -1; dx <= 1; dx++)
        {
            if (dx == 0 && dy == 0) continue;
            sum += _world.GetElevation(x + dx, y + dy);
            n++;
        }
        return n == 0 ? 0f : sum / n;
    }
}
