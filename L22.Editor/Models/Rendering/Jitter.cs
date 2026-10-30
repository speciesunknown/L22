namespace L22.Editor.Models.Rendering;

// Deterministic per-vertex XZ jitter so neighbouring chunks agree on shared
// edges. The scale is intentionally small — just enough to break up the
// grid-aligned triangle silhouettes.
public static class Jitter
{
    public const float Scale = 0.35f;

    public static (float dx, float dz) Offset(int worldVertexX, int worldVertexY)
    {
        uint hx = Hash((uint)worldVertexX * 374761393u ^ (uint)worldVertexY * 668265263u);
        uint hz = Hash(hx ^ 0x9E3779B9u);
        float dx = ((hx & 0xFFFF) / 65535f - 0.5f) * 2f * Scale;
        float dz = ((hz & 0xFFFF) / 65535f - 0.5f) * 2f * Scale;
        return (dx, dz);
    }

    private static uint Hash(uint x)
    {
        x = (x ^ 61u) ^ (x >> 16);
        x += x << 3;
        x ^= x >> 4;
        x *= 0x27d4eb2du;
        x ^= x >> 15;
        return x;
    }
}
