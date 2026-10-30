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
    private int _frustumVao, _frustumVbo, _frustumEbo;
    private bool _frustumInitialized;

    private int _cornerProgram;
    private int _uCornerView, _uCornerProj, _uCornerPointSize;
    private int _uCornerRingMode, _uCornerColor;
    private int _cornerVao, _cornerVbo;
    private int _cornerCount;
    private int _selVao, _selVbo;
    private int _selCount;

    private int _cubeProgram;
    private int _uCubeView, _uCubeProj, _uCubeCenter, _uCubeSize, _uCubeColor;
    private int _cubeVao, _cubeVbo, _cubeEbo;
    private bool _cubeInitialized;

    private int _billboardProgram;
    private int _uBbView, _uBbProj, _uBbCenter, _uBbSize, _uBbUvRect, _uBbTex;
    private int _billboardVao, _billboardVbo;
    private bool _billboardInitialized;
    // Icon path → GL texture name. 0 means we tried and failed to load
    // (missing file, bad PNG). Negative-cached so we don't thrash the disk
    // every frame on broken IconRef.ImagePath values.
    private readonly Dictionary<string, int> _iconTextures = new();

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

        _cubeProgram = CompileLink(gl, ShaderSource.CubeVertex, ShaderSource.CubeFragment);
        _uCubeView = gl.GetUniformLocationString(_cubeProgram, "uView");
        _uCubeProj = gl.GetUniformLocationString(_cubeProgram, "uProj");
        _uCubeCenter = gl.GetUniformLocationString(_cubeProgram, "uCenter");
        _uCubeSize = gl.GetUniformLocationString(_cubeProgram, "uSize");
        _uCubeColor = gl.GetUniformLocationString(_cubeProgram, "uColor");

        _billboardProgram = CompileLink(gl, ShaderSource.BillboardVertex, ShaderSource.BillboardFragment);
        _uBbView = gl.GetUniformLocationString(_billboardProgram, "uView");
        _uBbProj = gl.GetUniformLocationString(_billboardProgram, "uProj");
        _uBbCenter = gl.GetUniformLocationString(_billboardProgram, "uCenter");
        _uBbSize = gl.GetUniformLocationString(_billboardProgram, "uSize");
        _uBbUvRect = gl.GetUniformLocationString(_billboardProgram, "uUvRect");
        _uBbTex = gl.GetUniformLocationString(_billboardProgram, "uTex");

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
                       bool hideUnset = false,
                       Vector3[]? frustumPoints = null,
                       GpuObjectInstance[]? objects = null)
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

        if (frustumPoints is { Length: 5 })
            DrawFrustum(gl, view, proj, frustumPoints);

        if (objects is { Length: > 0 })
            DrawObjects(gl, view, proj, objects, keepDepthTest: hideUnset);

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

    // Unit cube centered at origin, 1 unit per side. 8 verts, 36 indices
    // (6 faces × 2 triangles). CCW winding so GL default front-face works.
    private static readonly float[] CubeVerts =
    {
        -0.5f,-0.5f,-0.5f,   0.5f,-0.5f,-0.5f,   0.5f, 0.5f,-0.5f,  -0.5f, 0.5f,-0.5f,  // back  face z=-0.5
        -0.5f,-0.5f, 0.5f,   0.5f,-0.5f, 0.5f,   0.5f, 0.5f, 0.5f,  -0.5f, 0.5f, 0.5f,  // front face z=+0.5
    };
    private static readonly uint[] CubeIndices =
    {
        0,2,1, 0,3,2,   // back
        4,5,6, 4,6,7,   // front
        0,7,3, 0,4,7,   // left
        1,2,6, 1,6,5,   // right
        0,1,5, 0,5,4,   // bottom
        3,7,6, 3,6,2,   // top
    };

    // Unit quad centered on origin — used for the billboard pass. Position
    // in local XY ([-0.5..0.5]), UV in [0..1].
    private static readonly float[] BillboardVerts =
    {
        -0.5f, -0.5f,   0f, 1f,
         0.5f, -0.5f,   1f, 1f,
         0.5f,  0.5f,   1f, 0f,
        -0.5f, -0.5f,   0f, 1f,
         0.5f,  0.5f,   1f, 0f,
        -0.5f,  0.5f,   0f, 0f,
    };

    private unsafe void DrawObjects(GlInterface gl, Matrix4x4 view, Matrix4x4 proj,
                                     GpuObjectInstance[] all, bool keepDepthTest)
    {
        EnsureCubeInitialized(gl);
        EnsureBillboardInitialized(gl);

        if (!keepDepthTest) gl.Disable(GL_DEPTH_TEST);

        // Billboards first — alpha-blended, so draw them over cubes (same
        // overlay layer anyway, order mostly matters for correctness of the
        // discard-based alpha fallback).
        bool anyBillboard = false;
        foreach (var o in all)
        {
            if (o.IconPath is null) continue;
            int tex = GetOrLoadIcon(gl, o.IconPath);
            if (tex == 0) continue;  // negative-cached; render as cube below

            if (!anyBillboard)
            {
                gl.UseProgram(_billboardProgram);
                SetUniformMatrix4(gl, _uBbView, view);
                SetUniformMatrix4(gl, _uBbProj, proj);
                gl.Uniform1i(_uBbTex, 0);  // sampler reads from unit 0
                _extras.ActiveTexture(GlExtras.GL_TEXTURE0);
                gl.Enable(GlExtras.GL_BLEND);
                _extras.BlendFunc(GlExtras.GL_SRC_ALPHA, GlExtras.GL_ONE_MINUS_SRC_ALPHA);
                gl.BindVertexArray(_billboardVao);
                anyBillboard = true;
            }

            gl.BindTexture(GlExtras.GL_TEXTURE_2D, tex);
            _extras.Uniform3f(_uBbCenter, o.Position.X, o.Position.Y, o.Position.Z);
            _extras.Uniform2f(_uBbSize, 2.5f, 2.5f);
            _extras.Uniform4f(_uBbUvRect,
                o.IconUvRect.X, o.IconUvRect.Y, o.IconUvRect.Z, o.IconUvRect.W);
            gl.DrawArrays(GL_TRIANGLES, 0, 6);
        }
        if (anyBillboard) gl.Disable(GlExtras.GL_BLEND);

        // Cubes for the fallback / failed-icon case.
        bool anyCube = false;
        foreach (var o in all)
        {
            bool needCube = o.IconPath is null
                            || GetOrLoadIcon(gl, o.IconPath) == 0;
            if (!needCube) continue;

            if (!anyCube)
            {
                gl.UseProgram(_cubeProgram);
                SetUniformMatrix4(gl, _uCubeView, view);
                SetUniformMatrix4(gl, _uCubeProj, proj);
                _extras.Uniform4f(_uCubeColor, 1.0f, 0.42f, 0.78f, 1.0f);
                gl.Uniform1f(_uCubeSize, 2.0f);
                gl.BindVertexArray(_cubeVao);
                anyCube = true;
            }
            _extras.Uniform3f(_uCubeCenter, o.Position.X, o.Position.Y, o.Position.Z);
            gl.DrawElements(GL_TRIANGLES, CubeIndices.Length, GlExtras.GL_UNSIGNED_INT, IntPtr.Zero);
        }

        if (!keepDepthTest) gl.Enable(GL_DEPTH_TEST);
    }

    private unsafe void EnsureCubeInitialized(GlInterface gl)
    {
        if (_cubeInitialized) return;
        _cubeVao = gl.GenVertexArray();
        _cubeVbo = gl.GenBuffer();
        _cubeEbo = gl.GenBuffer();
        gl.BindVertexArray(_cubeVao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _cubeVbo);
        fixed (float* vp = CubeVerts)
            gl.BufferData(GL_ARRAY_BUFFER,
                (IntPtr)(CubeVerts.Length * sizeof(float)),
                (IntPtr)vp, GL_STATIC_DRAW);
        gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, _cubeEbo);
        fixed (uint* ip = CubeIndices)
            gl.BufferData(GL_ELEMENT_ARRAY_BUFFER,
                (IntPtr)(CubeIndices.Length * sizeof(uint)),
                (IntPtr)ip, GL_STATIC_DRAW);
        gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 3 * sizeof(float), IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
        _cubeInitialized = true;
    }

    private unsafe void EnsureBillboardInitialized(GlInterface gl)
    {
        if (_billboardInitialized) return;
        _billboardVao = gl.GenVertexArray();
        _billboardVbo = gl.GenBuffer();
        gl.BindVertexArray(_billboardVao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _billboardVbo);
        fixed (float* vp = BillboardVerts)
            gl.BufferData(GL_ARRAY_BUFFER,
                (IntPtr)(BillboardVerts.Length * sizeof(float)),
                (IntPtr)vp, GL_STATIC_DRAW);
        int stride = 4 * sizeof(float);
        gl.VertexAttribPointer(0, 2, GL_FLOAT, 0, stride, IntPtr.Zero);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 2, GL_FLOAT, 0, stride, (IntPtr)(2 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        _billboardInitialized = true;
    }

    private unsafe int GetOrLoadIcon(GlInterface gl, string path)
    {
        if (_iconTextures.TryGetValue(path, out var cached)) return cached;
        int tex;
        try
        {
            var (rgba, w, h) = PngReader.ReadRgba(path);
            tex = _extras.GenTexture(gl);
            gl.BindTexture(GlExtras.GL_TEXTURE_2D, tex);
            fixed (byte* p = rgba)
                _extras.TexImage2D(GlExtras.GL_TEXTURE_2D, 0, GlExtras.GL_RGBA, w, h, 0,
                    GlExtras.GL_RGBA, GL_UNSIGNED_BYTE, (IntPtr)p);
            _extras.TexParameteri(GlExtras.GL_TEXTURE_2D, GlExtras.GL_TEXTURE_MIN_FILTER, GlExtras.GL_NEAREST);
            _extras.TexParameteri(GlExtras.GL_TEXTURE_2D, GlExtras.GL_TEXTURE_MAG_FILTER, GlExtras.GL_NEAREST);
            _extras.TexParameteri(GlExtras.GL_TEXTURE_2D, GlExtras.GL_TEXTURE_WRAP_S, GlExtras.GL_CLAMP_TO_EDGE);
            _extras.TexParameteri(GlExtras.GL_TEXTURE_2D, GlExtras.GL_TEXTURE_WRAP_T, GlExtras.GL_CLAMP_TO_EDGE);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[icon] failed to load '{path}': {ex.Message}");
            tex = 0;
        }
        _iconTextures[path] = tex;
        return tex;
    }

    // 8 line segments: apex→each ground corner, then the ground-plane ring.
    // Vertex order expected: [0]=apex, [1]=TL, [2]=TR, [3]=BR, [4]=BL.
    private static readonly uint[] FrustumIndices =
    {
        0, 1,  0, 2,  0, 3,  0, 4,     // apex spokes
        1, 2,  2, 3,  3, 4,  4, 1,     // ground ring
    };

    private unsafe void DrawFrustum(GlInterface gl, Matrix4x4 view, Matrix4x4 proj, Vector3[] pts)
    {
        if (!_frustumInitialized)
        {
            _frustumVao = gl.GenVertexArray();
            _frustumVbo = gl.GenBuffer();
            _frustumEbo = gl.GenBuffer();

            gl.BindVertexArray(_frustumVao);
            gl.BindBuffer(GL_ARRAY_BUFFER, _frustumVbo);
            gl.BindBuffer(GL_ELEMENT_ARRAY_BUFFER, _frustumEbo);
            fixed (uint* ip = FrustumIndices)
                gl.BufferData(GL_ELEMENT_ARRAY_BUFFER,
                    (IntPtr)(FrustumIndices.Length * sizeof(uint)),
                    (IntPtr)ip, GL_STATIC_DRAW);
            gl.VertexAttribPointer(0, 3, GL_FLOAT, 0, 3 * sizeof(float), IntPtr.Zero);
            gl.EnableVertexAttribArray(0);
            _frustumInitialized = true;
        }

        Span<float> verts = stackalloc float[5 * 3];
        for (int i = 0; i < 5; i++)
        {
            verts[i * 3 + 0] = pts[i].X;
            verts[i * 3 + 1] = pts[i].Y;
            verts[i * 3 + 2] = pts[i].Z;
        }
        gl.BindVertexArray(_frustumVao);
        gl.BindBuffer(GL_ARRAY_BUFFER, _frustumVbo);
        fixed (float* vp = verts)
            gl.BufferData(GL_ARRAY_BUFFER,
                (IntPtr)(verts.Length * sizeof(float)),
                (IntPtr)vp, GlExtras.GL_DYNAMIC_DRAW);

        gl.Disable(GL_DEPTH_TEST);
        gl.UseProgram(_wireProgram);
        SetUniformMatrix4(gl, _uWireView, view);
        SetUniformMatrix4(gl, _uWireProj, proj);
        _extras.Uniform4f(_uWireColor, 1.0f, 0.93f, 0.33f, 1.0f);
        gl.DrawElements(GlExtras.GL_LINES, FrustumIndices.Length, GlExtras.GL_UNSIGNED_INT, IntPtr.Zero);
        gl.Enable(GL_DEPTH_TEST);
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
        if (_cubeProgram != 0) gl.DeleteProgram(_cubeProgram);
        if (_frustumInitialized)
        {
            if (_frustumEbo != 0) gl.DeleteBuffer(_frustumEbo);
            if (_frustumVbo != 0) gl.DeleteBuffer(_frustumVbo);
            if (_frustumVao != 0) gl.DeleteVertexArray(_frustumVao);
            _frustumEbo = _frustumVbo = _frustumVao = 0;
            _frustumInitialized = false;
        }
        if (_cubeInitialized)
        {
            if (_cubeEbo != 0) gl.DeleteBuffer(_cubeEbo);
            if (_cubeVbo != 0) gl.DeleteBuffer(_cubeVbo);
            if (_cubeVao != 0) gl.DeleteVertexArray(_cubeVao);
            _cubeEbo = _cubeVbo = _cubeVao = 0;
            _cubeInitialized = false;
        }
        _terrainProgram = 0;
        _wireProgram = 0;
        _cornerProgram = 0;
        _cubeProgram = 0;
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
