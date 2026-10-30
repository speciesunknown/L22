using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;
using L22.Editor.Models.Rendering;
using L22.Editor.Models.World;
using static Avalonia.OpenGL.GlConsts;

namespace L22.Editor.Views;

// Self-contained GL state for one viewport: shaders, per-chunk VBOs, brush
// cursor rendering. Rebuilds dirty chunk meshes lazily on render.
public sealed class TerrainGlRenderer
{
    private World _world;
    private World? _pendingNewWorld;

    private int _terrainProgram;
    private int _uView, _uProj, _uBrushCenter, _uBrushRadius, _uBrushVisible;
    private int _uGreyscale, _uElevationScale, _uGreyLowY, _uGreyHighY, _uHideUnset;

    private int _wireProgram;
    private int _uWireView, _uWireProj, _uWireColor;

    private int _cornerProgram;
    private int _uCornerView, _uCornerProj, _uCornerPointSize;
    private int _uCornerRingMode, _uCornerColor;
    private int _cornerVao, _cornerVbo;
    private int _cornerCount;
    private int _selVao, _selVbo;
    private int _selCount;

    private readonly Dictionary<ChunkCoord, GpuChunk> _chunks = new();
    private readonly HashSet<ChunkCoord> _knownDirty = new();
    private readonly GlExtras _extras = new();

    public GlExtras Extras => _extras;
    public bool Initialized { get; private set; }
    public World World => _world;

    public TerrainGlRenderer(World world)
    {
        _world = world;
    }

    // Called when the viewport is re-bound to a different document. Can't
    // touch GL state here (no current context) — swap happens on the next
    // render call where GL is live.
    public void QueueRebind(World newWorld) => _pendingNewWorld = newWorld;

    public void Init(GlInterface gl)
    {
        _extras.Resolve(gl);

        _terrainProgram = CompileLink(gl, ShaderSource.TerrainVertex, ShaderSource.TerrainFragment);
        _uView = gl.GetUniformLocationString(_terrainProgram, "uView");
        _uProj = gl.GetUniformLocationString(_terrainProgram, "uProj");
        _uBrushCenter = gl.GetUniformLocationString(_terrainProgram, "uBrushCenter");
        _uBrushRadius = gl.GetUniformLocationString(_terrainProgram, "uBrushRadius");
        _uBrushVisible = gl.GetUniformLocationString(_terrainProgram, "uBrushVisible");
        _uGreyscale = gl.GetUniformLocationString(_terrainProgram, "uGreyscale");
        _uElevationScale = gl.GetUniformLocationString(_terrainProgram, "uElevationScale");
        _uGreyLowY = gl.GetUniformLocationString(_terrainProgram, "uGreyLowY");
        _uGreyHighY = gl.GetUniformLocationString(_terrainProgram, "uGreyHighY");
        _uHideUnset = gl.GetUniformLocationString(_terrainProgram, "uHideUnset");

        _wireProgram = CompileLink(gl, ShaderSource.GridVertex, ShaderSource.GridFragment);
        _uWireView = gl.GetUniformLocationString(_wireProgram, "uView");
        _uWireProj = gl.GetUniformLocationString(_wireProgram, "uProj");
        _uWireColor = gl.GetUniformLocationString(_wireProgram, "uLineColor");

        _cornerProgram = CompileLink(gl, ShaderSource.CornerPointVertex, ShaderSource.CornerPointFragment);
        _uCornerView = gl.GetUniformLocationString(_cornerProgram, "uView");
        _uCornerProj = gl.GetUniformLocationString(_cornerProgram, "uProj");
        _uCornerPointSize = gl.GetUniformLocationString(_cornerProgram, "uPointSize");
        _uCornerRingMode = gl.GetUniformLocationString(_cornerProgram, "uRingMode");
        _uCornerColor = gl.GetUniformLocationString(_cornerProgram, "uColor");
        _cornerVao = gl.GenVertexArray();
        _cornerVbo = gl.GenBuffer();
        _selVao = gl.GenVertexArray();
        _selVbo = gl.GenBuffer();

        foreach (var c in _world.Chunks.Values) _knownDirty.Add(c.Coord);
        Initialized = true;
    }

    public void MarkChunkDirty(ChunkCoord c) => _knownDirty.Add(c);

    public void Render(GlInterface gl, int width, int height,
                       Matrix4x4 view, Matrix4x4 proj,
                       Vector3 brushCenterWorld, float brushRadiusWorld, bool brushVisible,
                       bool greyscale,
                       bool showCornerMarkers = false,
                       float cornerMarkerSize = 10f,
                       IReadOnlyCollection<(int cx, int cy)>? selectedCorners = null,
                       float greyLowY = 0f,
                       float greyHighY = 14f,
                       bool hideUnset = false)
    {
        // Apply a pending world swap now that we have a live GL context.
        if (_pendingNewWorld is { } nextWorld)
        {
            foreach (var gpu in _chunks.Values) gpu.Destroy(gl);
            _chunks.Clear();
            _knownDirty.Clear();
            _world = nextWorld;
            foreach (var c in _world.Chunks.Values) _knownDirty.Add(c.Coord);
            _pendingNewWorld = null;
        }

        gl.Viewport(0, 0, width, height);
        gl.Enable(GL_DEPTH_TEST);
        gl.ClearColor(0.42f, 0.58f, 0.78f, 1f);
        gl.Clear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);

        // Rebuild dirty chunk GPU buffers. Snapshot both collections before
        // iterating because mesh build can observe shared state the world may
        // mutate on the next event-loop tick, and a dirty-list that grew
        // during a late event would surprise us.
        foreach (var c in _world.Chunks.Values)
            if (c.MeshDirty) _knownDirty.Add(c.Coord);
        var toBuild = _knownDirty.ToArray();
        _knownDirty.Clear();
        foreach (var cc in toBuild)
        {
            var ch = _world.TryGetChunk(cc);
            if (ch is null) continue;
            if (!_chunks.TryGetValue(cc, out var gpu))
            {
                gpu = new GpuChunk();
                gpu.Create(gl);
                _chunks[cc] = gpu;
            }
            var mesh = TerrainMeshBuilder.Build(_world, ch);
            gpu.Upload(gl, mesh);
            ch.MeshDirty = false;
        }

        gl.UseProgram(_terrainProgram);
        SetUniformMatrix4(gl, _uView, view);
        SetUniformMatrix4(gl, _uProj, proj);
        _extras.Uniform3f(_uBrushCenter, brushCenterWorld.X, brushCenterWorld.Y, brushCenterWorld.Z);
        gl.Uniform1f(_uBrushRadius, brushRadiusWorld);
        gl.Uniform1i(_uBrushVisible, brushVisible ? 1 : 0);
        gl.Uniform1i(_uGreyscale, greyscale ? 1 : 0);
        gl.Uniform1f(_uElevationScale, Models.Rendering.TerrainMeshBuilder.ElevationScale);
        gl.Uniform1f(_uGreyLowY, greyLowY);
        gl.Uniform1f(_uGreyHighY, greyHighY);
        gl.Uniform1i(_uHideUnset, hideUnset ? 1 : 0);

        foreach (var gpu in _chunks.Values) gpu.Draw(gl);

        // Wireframe pass: cells with no terrain show as line edges so the
        // user sees "no geometry here" instead of a filled surface. Skipped
        // in the 3D view where unset cells are hidden entirely.
        if (!hideUnset)
        {
            gl.UseProgram(_wireProgram);
            SetUniformMatrix4(gl, _uWireView, view);
            SetUniformMatrix4(gl, _uWireProj, proj);
            _extras.Uniform4f(_uWireColor, 0.88f, 0.90f, 0.95f, 1.0f);
            foreach (var gpu in _chunks.Values) gpu.DrawWire(gl);
        }

        if (showCornerMarkers)
        {
            // Overlay — never depth-clipped so corners show even when a
            // chunk peaks up in front of them.
            gl.Disable(GL_DEPTH_TEST);

            UploadCornerMarkerVbo(gl);
            gl.UseProgram(_cornerProgram);
            SetUniformMatrix4(gl, _uCornerView, view);
            SetUniformMatrix4(gl, _uCornerProj, proj);

            // Pass 1: pink filled dots on every chunk corner.
            gl.Uniform1f(_uCornerPointSize, cornerMarkerSize);
            gl.Uniform1i(_uCornerRingMode, 0);
            _extras.Uniform4f(_uCornerColor, 1.0f, 0.3f, 0.7f, 1.0f);
            gl.BindVertexArray(_cornerVao);
            gl.DrawArrays(GlExtras.GL_POINTS, 0, _cornerCount);

            // Pass 2: yellow outline rings on selected corners.
            if (selectedCorners is { Count: > 0 })
            {
                UploadSelectionVbo(gl, selectedCorners);
                gl.Uniform1f(_uCornerPointSize, cornerMarkerSize + 10f);
                gl.Uniform1i(_uCornerRingMode, 1);
                _extras.Uniform4f(_uCornerColor, 1.0f, 0.95f, 0.25f, 1.0f);
                gl.BindVertexArray(_selVao);
                gl.DrawArrays(GlExtras.GL_POINTS, 0, _selCount);
            }

            gl.Enable(GL_DEPTH_TEST);
        }
    }

    private unsafe void UploadSelectionVbo(GlInterface gl, IReadOnlyCollection<(int cx, int cy)> selected)
    {
        var pts = new List<float>(selected.Count * 3);
        foreach (var (cx, cy) in selected)
        {
            int wx = cx * Chunk.Size;
            int wy = cy * Chunk.Size;
            var (jx, jz) = Models.Rendering.Jitter.Offset(wx, wy);
            float y = Models.Rendering.TerrainMeshBuilder.SampleChunkCornerBilerp(_world, wx, wy)
                      * Models.Rendering.TerrainMeshBuilder.ElevationScale + 0.2f;
            pts.Add(wx + jx);
            pts.Add(y);
            pts.Add(wy + jz);
        }
        _selCount = pts.Count / 3;
        if (_selCount == 0) return;
        gl.BindVertexArray(_selVao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _selVbo);
        var arr = pts.ToArray();
        fixed (float* p = arr)
            gl.BufferData(GL_ARRAY_BUFFER,
                (IntPtr)(arr.Length * sizeof(float)),
                (IntPtr)p, GL_STATIC_DRAW);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 3 * sizeof(float), IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
    }

    private unsafe void UploadCornerMarkerVbo(GlInterface gl)
    {
        var pts = new List<float>(64);
        foreach (var (cx, cy) in _world.EnumerateChunkCorners())
        {
            int wx = cx * Chunk.Size;
            int wy = cy * Chunk.Size;
            var (jx, jz) = Models.Rendering.Jitter.Offset(wx, wy);
            float y = Models.Rendering.TerrainMeshBuilder.SampleChunkCornerBilerp(_world, wx, wy)
                      * Models.Rendering.TerrainMeshBuilder.ElevationScale
                      + 0.15f;  // lift slightly so the point reads above the surface
            pts.Add(wx + jx);
            pts.Add(y);
            pts.Add(wy + jz);
        }

        _cornerCount = pts.Count / 3;
        if (_cornerCount == 0) return;

        gl.BindVertexArray(_cornerVao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _cornerVbo);
        var arr = pts.ToArray();
        fixed (float* p = arr)
            gl.BufferData(GL_ARRAY_BUFFER,
                (IntPtr)(arr.Length * sizeof(float)),
                (IntPtr)p, GL_STATIC_DRAW);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 3 * sizeof(float), IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
    }

    public void Deinit(GlInterface gl)
    {
        foreach (var gpu in _chunks.Values) gpu.Destroy(gl);
        _chunks.Clear();
        if (_terrainProgram != 0) gl.DeleteProgram(_terrainProgram);
        if (_wireProgram != 0) gl.DeleteProgram(_wireProgram);
        if (_cornerProgram != 0) gl.DeleteProgram(_cornerProgram);
        _terrainProgram = 0;
        _wireProgram = 0;
        _cornerProgram = 0;
        Initialized = false;
    }

    private static int CompileLink(GlInterface gl, string vsSrc, string fsSrc)
    {
        int vs = CompileShader(gl, GL_VERTEX_SHADER, vsSrc);
        int fs = CompileShader(gl, GL_FRAGMENT_SHADER, fsSrc);
        int prog = gl.CreateProgram();
        gl.AttachShader(prog, vs);
        gl.AttachShader(prog, fs);
        gl.LinkProgram(prog);
        var log = ReadProgramLog(gl, prog);
        if (!string.IsNullOrWhiteSpace(log))
            Console.Error.WriteLine($"[GL] program link log: {log}");
        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return prog;
    }

    private static int CompileShader(GlInterface gl, int type, string src)
    {
        int s = gl.CreateShader(type);
        gl.ShaderSourceString(s, src);
        gl.CompileShader(s);
        var log = ReadShaderLog(gl, s);
        if (!string.IsNullOrWhiteSpace(log))
            Console.Error.WriteLine($"[GL] shader compile log ({type}): {log}");
        return s;
    }

    private static unsafe string ReadShaderLog(GlInterface gl, int shader)
    {
        var buf = new byte[2048];
        int len;
        fixed (byte* p = buf)
            gl.GetShaderInfoLog(shader, buf.Length, out len, p);
        return len <= 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(buf, 0, Math.Min(len, buf.Length));
    }

    private static unsafe string ReadProgramLog(GlInterface gl, int program)
    {
        var buf = new byte[2048];
        int len;
        fixed (byte* p = buf)
            gl.GetProgramInfoLog(program, buf.Length, out len, p);
        return len <= 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(buf, 0, Math.Min(len, buf.Length));
    }

    private static unsafe void SetUniformMatrix4(GlInterface gl, int loc, Matrix4x4 m)
    {
        Span<float> f = stackalloc float[16];
        f[0] = m.M11; f[1] = m.M12; f[2] = m.M13; f[3] = m.M14;
        f[4] = m.M21; f[5] = m.M22; f[6] = m.M23; f[7] = m.M24;
        f[8] = m.M31; f[9] = m.M32; f[10] = m.M33; f[11] = m.M34;
        f[12] = m.M41; f[13] = m.M42; f[14] = m.M43; f[15] = m.M44;
        fixed (float* p = f)
            gl.UniformMatrix4fv(loc, 1, false, p);
    }

    private sealed class GpuChunk
    {
        public int Vao, Vbo, Ebo;
        public int WireVao, WireEbo;
        public int IndexCount;
        public int WireIndexCount;
        public int VertexCount;

        public void Create(GlInterface gl)
        {
            Vao = gl.GenVertexArray();
            Vbo = gl.GenBuffer();
            Ebo = gl.GenBuffer();
            WireVao = gl.GenVertexArray();
            WireEbo = gl.GenBuffer();
        }

        public unsafe void Upload(GlInterface gl, ChunkMeshData mesh)
        {
            // Shared VBO: upload once, bind from both VAOs below.
            gl.BindBuffer(GL_ARRAY_BUFFER, Vbo);
            fixed (float* p = mesh.Vertices)
                gl.BufferData(GL_ARRAY_BUFFER,
                    (IntPtr)(mesh.Vertices.Length * sizeof(float)),
                    (IntPtr)p, GL_STATIC_DRAW);

            int stride = 9 * sizeof(float);

            // Fill VAO: pos + color + normal, triangle EBO.
            gl.BindVertexArray(Vao);
            gl.BindBuffer(GL_ARRAY_BUFFER, Vbo);
            gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, Ebo);
            fixed (uint* p = mesh.Indices)
                gl.BufferData(GL_ELEMENT_ARRAY_BUFFER,
                    (IntPtr)(mesh.Indices.Length * sizeof(uint)),
                    (IntPtr)p, GL_STATIC_DRAW);
            gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, stride, IntPtr.Zero);
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(1, 3, GL_FLOAT, 0, stride, (IntPtr)(3 * sizeof(float)));
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(2, 3, GL_FLOAT, 0, stride, (IntPtr)(6 * sizeof(float)));
            gl.EnableVertexAttribArray(2);

            // Wire VAO: only pos needed, line EBO. Separate VAO so the EBO
            // binding doesn't fight with the fill pass.
            gl.BindVertexArray(WireVao);
            gl.BindBuffer(GL_ARRAY_BUFFER, Vbo);
            gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, WireEbo);
            fixed (uint* p = mesh.WireIndices)
                gl.BufferData(GL_ELEMENT_ARRAY_BUFFER,
                    (IntPtr)(mesh.WireIndices.Length * sizeof(uint)),
                    (IntPtr)p, GL_STATIC_DRAW);
            gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, stride, IntPtr.Zero);
            gl.EnableVertexAttribArray(0);

            IndexCount = mesh.Indices.Length;
            WireIndexCount = mesh.WireIndices.Length;
            VertexCount = mesh.Vertices.Length / mesh.VertexFloatCount;
        }

        public void Draw(GlInterface gl)
        {
            if (IndexCount == 0) return;
            gl.BindVertexArray(Vao);
            gl.DrawElements(GL_TRIANGLES, IndexCount, GlExtras.GL_UNSIGNED_INT, IntPtr.Zero);
        }

        public void DrawWire(GlInterface gl)
        {
            if (WireIndexCount == 0) return;
            gl.BindVertexArray(WireVao);
            gl.DrawElements(GlExtras.GL_LINES, WireIndexCount, GlExtras.GL_UNSIGNED_INT, IntPtr.Zero);
        }

        public void DrawPoints(GlInterface gl)
        {
            if (VertexCount == 0) return;
            gl.BindVertexArray(Vao);
            gl.DrawArrays(GlExtras.GL_POINTS, 0, VertexCount);
        }

        public void Destroy(GlInterface gl)
        {
            if (Vbo != 0) gl.DeleteBuffer(Vbo);
            if (Ebo != 0) gl.DeleteBuffer(Ebo);
            if (WireEbo != 0) gl.DeleteBuffer(WireEbo);
            if (Vao != 0) gl.DeleteVertexArray(Vao);
            if (WireVao != 0) gl.DeleteVertexArray(WireVao);
            Vbo = Ebo = WireEbo = Vao = WireVao = 0;
            IndexCount = 0;
            WireIndexCount = 0;
        }
    }
}
