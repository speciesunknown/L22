using System.Numerics;
using L22.Editor.Models.Rendering;

namespace L22.Editor.Models;

// Per-tab camera state. Each OpenDocument keeps one snapshot per viewport so
// switching tabs restores the exact view the user left behind.
public struct CameraSnapshot
{
    public Vector3 Position;
    public float Yaw;
    public float Pitch;
    public float OrthoHalfHeight;
    public bool IsOrthographic;

    public static CameraSnapshot From(Camera c) => new()
    {
        Position = c.Position,
        Yaw = c.Yaw,
        Pitch = c.Pitch,
        OrthoHalfHeight = c.OrthoHalfHeight,
        IsOrthographic = c.IsOrthographic,
    };

    public void ApplyTo(Camera c)
    {
        c.Position = Position;
        c.Yaw = Yaw;
        c.Pitch = Pitch;
        c.OrthoHalfHeight = OrthoHalfHeight;
        c.IsOrthographic = IsOrthographic;
    }
}
