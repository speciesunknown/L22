namespace L22.Editor.Views;

// CPU-built river mesh handed to TerrainGlRenderer each frame. One strip
// per river/branch is combined into a single VBO + EBO payload here; the
// renderer uploads them in one shot.
public sealed class GpuRiverMesh
{
    public float[] Vertices = System.Array.Empty<float>();  // pos3
    public uint[] Indices = System.Array.Empty<uint>();
}
