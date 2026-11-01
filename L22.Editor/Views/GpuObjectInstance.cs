using System.Numerics;

namespace L22.Editor.Views;

// Per-object render payload the TerrainGlRenderer consumes. When IconPath
// is null the renderer falls back to the pink cube; otherwise it loads/caches
// the texture and draws a camera-facing billboard with the given UV sub-rect.
public readonly record struct GpuObjectInstance(
    Vector3 Position,
    string? IconPath,
    Vector4 IconUvRect,
    bool Selected = false);
