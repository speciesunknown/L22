using System;
using System.Collections.Generic;
using System.Numerics;

namespace L22.Editor.Models.Rivers;

// Samples each river's segments as cubic bezier curves and caches the
// result for a build pass. The mesh builder's erosion math + the top-down
// overlay + the GL strip mesh all read from the same list so the curve
// stays consistent.
//
// Per-segment cubic:
//   start      = vi
//   startCtrl  = vi + vi.BezierHandleOut
//   endCtrl    = vi+1 - vi+1.BezierHandleOut   (mirrored — only one handle
//                                               per vertex is UI-editable)
//   end        = vi+1
// Width is linearly interpolated along the segment.
public static class BezierSampler
{
    public const int SegmentSteps = 12;

    public readonly record struct SampledPoint(float X, float Z, float Width);

    public static List<SampledPoint> Sample(River river)
    {
        var pts = new List<SampledPoint>();
        if (river.Vertices.Count == 0) return pts;
        if (river.Vertices.Count == 1)
        {
            var v = river.Vertices[0];
            pts.Add(new SampledPoint(v.WorldX, v.WorldZ, v.WidthWorld));
            return pts;
        }
        for (int i = 0; i < river.Vertices.Count - 1; i++)
        {
            var a = river.Vertices[i];
            var b = river.Vertices[i + 1];
            var p0 = new Vector2(a.WorldX, a.WorldZ);
            var p3 = new Vector2(b.WorldX, b.WorldZ);
            var p1 = p0 + a.BezierHandleOut;
            var p2 = p3 - b.BezierHandleOut;

            int startStep = i == 0 ? 0 : 1;  // avoid duplicating interior joint
            for (int s = startStep; s <= SegmentSteps; s++)
            {
                float t = s / (float)SegmentSteps;
                float u = 1f - t;
                var p = u * u * u * p0
                      + 3f * u * u * t * p1
                      + 3f * u * t * t * p2
                      + t * t * t * p3;
                float w = u * a.WidthWorld + t * b.WidthWorld;
                pts.Add(new SampledPoint(p.X, p.Y, w));
            }
        }
        return pts;
    }

    // Walks a river tree flattening to one list of per-tree sampled
    // segments. Each entry is "where the bank sits" for the influence /
    // ribbon / overlay passes.
    public static List<List<SampledPoint>> SampleTree(River river)
    {
        var all = new List<List<SampledPoint>>();
        Recurse(river, all);
        return all;
    }
    private static void Recurse(River r, List<List<SampledPoint>> acc)
    {
        acc.Add(Sample(r));
        foreach (var b in r.Branches) Recurse(b, acc);
    }

    // Max influence over every polyline in `samples` at the world point
    // (wx, wz). 1 at centreline, 0 at or beyond the half-width edge.
    public static float Influence(IReadOnlyList<IReadOnlyList<SampledPoint>> samples, float wx, float wz)
    {
        float best = 0f;
        foreach (var line in samples)
        {
            float f = InfluenceOnLine(line, wx, wz);
            if (f > best) best = f;
        }
        return best;
    }

    private static float InfluenceOnLine(IReadOnlyList<SampledPoint> line, float wx, float wz)
    {
        float best = 0f;
        for (int i = 0; i < line.Count - 1; i++)
        {
            var a = line[i];
            var b = line[i + 1];
            float dx = b.X - a.X, dz = b.Z - a.Z;
            float len2 = dx * dx + dz * dz;
            float t = len2 > 1e-6f
                ? MathF.Max(0, MathF.Min(1, ((wx - a.X) * dx + (wz - a.Z) * dz) / len2))
                : 0f;
            float px = a.X + dx * t;
            float pz = a.Z + dz * t;
            float dxp = wx - px, dzp = wz - pz;
            float dist = MathF.Sqrt(dxp * dxp + dzp * dzp);
            float width = (1 - t) * a.Width + t * b.Width;
            float half = MathF.Max(0.01f, width * 0.5f);
            if (dist < half)
            {
                float linear = 1f - (dist / half);
                float influence = linear * linear * (3f - 2f * linear);
                if (influence > best) best = influence;
            }
        }
        return best;
    }
}
