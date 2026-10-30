using System;
using System.Numerics;

namespace L22.Editor.Models.Rendering;

public sealed class Camera
{
    public Vector3 Position { get; set; } = new(16, 20, 48);

    // Yaw rotates around world Y (left/right). Pitch rotates around local X
    // (up/down). Both are in radians.
    public float Yaw { get; set; } = -MathF.PI * 0.1f;
    public float Pitch { get; set; } = -0.5f;

    public float FovY { get; set; } = MathF.PI / 3f;  // 60 deg
    public float NearPlane { get; set; } = 0.1f;
    public float FarPlane { get; set; } = 500f;

    // Orthographic mode: used by the top-down painting viewport. The ortho
    // frustum is centered at Position; half-height is in world units.
    public bool IsOrthographic { get; set; }
    public float OrthoHalfHeight { get; set; } = 64f;

    public Vector3 Forward
    {
        get
        {
            float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
            float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);
            return Vector3.Normalize(new Vector3(sy * cp, sp, -cy * cp));
        }
    }

    public Vector3 Right
    {
        get
        {
            float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);
            return Vector3.Normalize(new Vector3(cy, 0, sy));
        }
    }

    public Vector3 Up => Vector3.Cross(Right, Forward);

    public Matrix4x4 View
    {
        get
        {
            // For strictly top-down ortho views, pitch is -90 which makes
            // (0,1,0) a degenerate up-vector. Pick a stable up (world -Z).
            var up = IsOrthographic && MathF.Abs(Pitch + MathF.PI / 2f) < 0.01f
                ? new Vector3(0, 0, -1)
                : Vector3.UnitY;
            return Matrix4x4.CreateLookAt(Position, Position + Forward, up);
        }
    }

    public Matrix4x4 Projection(float aspect)
    {
        if (IsOrthographic)
        {
            float h = MathF.Max(0.1f, OrthoHalfHeight);
            float w = h * MathF.Max(0.01f, aspect);
            return Matrix4x4.CreateOrthographicOffCenter(-w, w, -h, h, -500f, 500f);
        }
        return Matrix4x4.CreatePerspectiveFieldOfView(FovY, MathF.Max(0.01f, aspect), NearPlane, FarPlane);
    }

    public void Move(Vector3 worldDelta) => Position += worldDelta;

    public void Rotate(float yawDelta, float pitchDelta)
    {
        Yaw += yawDelta;
        Pitch = Math.Clamp(Pitch + pitchDelta, -MathF.PI / 2f + 0.01f, MathF.PI / 2f - 0.01f);
    }

    // Cast a ray from screen space (0..1 ndc-ish) into world space.
    // sx/sy are in [-1, 1] with (-1,-1) at bottom-left of the viewport.
    public (Vector3 origin, Vector3 dir) ScreenRay(float sx, float sy, float aspect)
    {
        var invProj = Matrix4x4.Invert(Projection(aspect), out var ip) ? ip : Matrix4x4.Identity;
        var invView = Matrix4x4.Invert(View, out var iv) ? iv : Matrix4x4.Identity;

        var nearNdc = new Vector4(sx, sy, -1f, 1f);
        var farNdc = new Vector4(sx, sy, 1f, 1f);

        var nearView = Vector4.Transform(nearNdc, invProj);
        var farView = Vector4.Transform(farNdc, invProj);
        nearView /= nearView.W;
        farView /= farView.W;

        var nearWorld = Vector4.Transform(nearView, invView);
        var farWorld = Vector4.Transform(farView, invView);

        var origin = new Vector3(nearWorld.X, nearWorld.Y, nearWorld.Z);
        var dir = Vector3.Normalize(new Vector3(farWorld.X - nearWorld.X,
                                                farWorld.Y - nearWorld.Y,
                                                farWorld.Z - nearWorld.Z));
        return (origin, dir);
    }
}
