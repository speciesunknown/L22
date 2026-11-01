using System;
using System.Collections.Generic;
using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Point = Avalonia.Point;
using L22.Editor.Models.Objects;
using L22.Editor.Models.Rendering;
using L22.Editor.Models.Rivers;
using L22.Editor.Models.Tools;
using L22.Editor.Models.World;
using static Avalonia.OpenGL.GlConsts;

namespace L22.Editor.Views;

public class TerrainViewport : OpenGlControlBase
{
    public Camera Camera { get; } = new();
    public World? World { get; set; }
    public ToolManager? Tools { get; set; }
    // Objects placed in the active scene; drawn as pink-cube fallbacks
    // when no icon is resolved and as camera-facing textured billboards
    // when one is.
    public System.Collections.Generic.IReadOnlyList<ObjectInstance>? Objects { get; set; }
    // Rivers to show in the overlay. Updated via BindContext.
    public System.Collections.Generic.IReadOnlyList<River>? Rivers { get; set; }
    // Prefab library the renderer resolves icon inheritance against. An
    // instance with no IconOverride falls back to its prefab's Icon.
    public PrefabLibrary? Prefabs { get; set; }
    public bool AllowPainting { get; set; } = false;

    // When set, this viewport's renderer draws the frustum of
    // FrustumSource.Camera as GL geometry (lines on the ground plane,
    // apex at the source camera's XZ, no depth testing).
    public TerrainViewport? FrustumSource { get; set; }

    // When set, every finished render pushes a RequestFrame to the other
    // viewport so overlays that depend on this one's camera stay in sync
    // without needing a wall-clock timer.
    public TerrainViewport? MirrorFramesTo { get; set; }

    // Top-down ortho mode: used by the painting viewport. Locks pitch to
    // straight down, swaps camera controls to pan/zoom.
    public bool TopDown { get; set; }

    // When true the renderer shades the ground by elevation (black=0, white=1)
    // instead of using terrain-type colors. Used while the Elevation layer is
    // active so brush strokes read as literal height.
    public bool ShowElevationGreyscale { get; set; }

    // Corner Elevation tool: paints a pink marker at every vertex and swaps
    // left-click behavior to rubber-band box selection.
    public bool CornerToolActive { get; set; }

    // Dropper tool: left-click samples the hovered cell's active-layer
    // value into the brush VM.
    public bool DropperToolActive { get; set; }
    public event Action<int, int>? CellSampled;

    // River tool: left-click in the top-down adds a vertex to the active
    // river at the picked cell (sub-cell position by exact mouse point).
    public bool RiverToolActive { get; set; }
    public event Action<float, float>? RiverVertexAddRequested;
    // Journey scenario 1: hit-test on an existing vertex handle emits this
    // (riverId, vertexIndex) so MainWindow can set it as the selection.
    public event Action<string, int>? RiverVertexSelectRequested;

    // Place-object tool: a click (no drag) drops a new ObjectInstance at
    // the picked cell; a drag rubber-bands a selection over objects in
    // the enclosed world-space rectangle.
    public bool PlaceObjectToolActive { get; set; }
    public event Action<int, int>? ObjectPlaceRequested;
    public event Action<float, float, float, float>? ObjectBoxSelectRequested;
    // Click on an existing object (no drag). Replaces selection with the
    // single hit instance.
    public event Action<string>? ObjectSingleSelectRequested;
    // Double-click on an existing object. Opens the editor.
    public event Action<string>? ObjectEditRequested;

    // Press state for the object tool's click-vs-drag discrimination.
    // Threshold is checked against raw pixels, not cells, so a scroll-
    // induced sub-pixel drift during a click doesn't trip the drag.
    private bool _objectGesturePressed;
    private bool _objectGestureDragging;
    private Point _objectGestureStart;
    private const double ObjectDragThresholdPx = 4.0;

    // Last committed selection (populated on box-select release) — chunk
    // corner-grid coords, not per-cell vertices. Consumed by the right
    // panel's Add/Override buttons.
    public HashSet<(int cx, int cy)> SelectedCorners { get; } = new();
    public event Action? SelectionChanged;

    // Rubber-band in viewport local coords while actively dragging. Null when
    // not selecting.
    public Avalonia.Rect? BoxSelectRect { get; private set; }
    public event Action? BoxSelectVisualChanged;
    private bool _boxSelecting;
    private Point _boxStart;
    private bool _shiftAtPressStart;

    // Free-camera mode entered via the on-viewport toggle. In this mode plain
    // pointer motion (no button) rotates (perspective) or pans (top-down).
    private bool _cameraFree;
    private bool _cameraFreeFirstMove;

    public bool CameraFree => _cameraFree;
    public event Action? CameraFreeChanged;

    public void SetCameraFree(bool on)
    {
        if (_cameraFree == on) return;
        _cameraFree = on;
        _cameraFreeFirstMove = true;
        _held.Clear();
        _rotating = false;
        _painting = false;
        CameraFreeChanged?.Invoke();
        RequestFrame();
    }

    private TerrainGlRenderer? _renderer;
    private readonly HashSet<Key> _held = new();
    private DispatcherTimer? _tickTimer;
    private DateTime _lastTick;

    private bool _rotating;
    private Point _rotateLast;
    private bool _painting;
    private Vector3 _lastBrushCenter = new(16, 0, 16);
    private bool _brushVisible;

    // Framebuffer capture.
    public bool CaptureRequested { get; set; }
    public byte[]? LastCaptureRgba { get; private set; }
    public int LastCaptureWidth { get; private set; }
    public int LastCaptureHeight { get; private set; }
    public event Action? CaptureCompleted;

    public TerrainViewport()
    {
        Focusable = true;
    }

    // Called by MainWindow so that keyboard focus doesn't need to live on the
    // GL control itself (which it struggles with). Routed to whichever
    // viewport the pointer is currently over.
    // Window dispatches here when this viewport is the "active" one (either
    // because the pointer is over it, or because it was last hovered).
    public void ReceiveKeyDown(KeyEventArgs e) => _held.Add(e.Key);
    public void ReceiveKeyUp(KeyEventArgs e)   => _held.Remove(e.Key);

    // Drop any still-held movement keys. Called when the window deactivates
    // or focus leaves the editor — key-up events are not delivered to a
    // backgrounded window, so without this a key stuck down at the moment
    // of Alt-Tab would keep moving the camera forever.
    public void ClearHeldKeys() => _held.Clear();

    public void BindWorld(World world, ToolManager? tools)
    {
        if (World is not null)
        {
            World.ChunkChanged -= OnWorldChunkChanged;
            World.ChunkCreated -= OnWorldChunkChanged;
        }
        World = world;
        Tools = tools;
        world.ChunkChanged += OnWorldChunkChanged;
        world.ChunkCreated += OnWorldChunkChanged;
        if (TopDown) ConfigureTopDown();

        // If the GL renderer is already initialized (we're re-binding to a
        // different document), tell it to swap worlds on the next render.
        _renderer?.QueueRebind(world);
        RequestFrame();
    }

    private EditorContext? _boundContext;
    public void BindContext(EditorContext ctx, bool allowTools = true)
    {
        if (_boundContext is not null)
        {
            _boundContext.ObjectsChanged -= OnObjectsChanged;
            _boundContext.Prefabs.PrefabsChanged -= OnObjectsChanged;
            _boundContext.ObjectSelectionChanged -= OnObjectsChanged;
            _boundContext.RiversChanged -= OnRiversChanged;
        }
        BindWorld(ctx.World, allowTools ? ctx.Tools : null);
        Objects = ctx.Objects;
        Prefabs = ctx.Prefabs;
        Rivers = ctx.Rivers;
        _boundContext = ctx;
        ctx.ObjectsChanged += OnObjectsChanged;
        ctx.Prefabs.PrefabsChanged += OnObjectsChanged;
        ctx.ObjectSelectionChanged += OnObjectsChanged;
        ctx.RiversChanged += OnRiversChanged;
    }

    // Rivers affect terrain geometry (Phase 4 erosion), so every chunk the
    // affected river could reach needs a re-mesh. Simplest: mark everything
    // dirty; chunk count is small and the mesh builder is cheap.
    private void OnRiversChanged()
    {
        if (World is not null)
            foreach (var ch in World.Chunks.Values) ch.MeshDirty = true;
        RequestFrame();
    }

    private void OnObjectsChanged() => RequestFrame();

    public void ConfigureTopDown()
    {
        Camera.IsOrthographic = true;
        Camera.Yaw = 0f;
        Camera.Pitch = -MathF.PI / 2f;
        Camera.Position = new System.Numerics.Vector3(64, 100, 64);
        Camera.OrthoHalfHeight = 72f;
    }

    private void EnforceTopDown()
    {
        // Hard lock: never let anything tilt the ortho camera.
        if (!TopDown) return;
        Camera.IsOrthographic = true;
        Camera.Yaw = 0f;
        Camera.Pitch = -MathF.PI / 2f;
    }

    private void OnWorldChunkChanged(Chunk ch)
    {
        _renderer?.MarkChunkDirty(ch.Coord);
        RequestFrame();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        EnsureTickTimer();
        // World may still be null here when the editor starts on the welcome
        // screen — the viewport becomes visible before any document is bound.
        // We defer renderer creation to the first render where World is set.
        if (World is null) return;
        _renderer = new TerrainGlRenderer(World);
        _renderer.Init(gl);
    }

    private void EnsureTickTimer()
    {
        _tickTimer?.Stop();
        _tickTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Background, OnTick);
        _lastTick = DateTime.UtcNow;
        _tickTimer.Start();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _tickTimer?.Stop();
        _tickTimer = null;
        _renderer?.Deinit(gl);
        _renderer = null;
    }

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (World is null) return;
        // Lazy renderer init: handles the case where OnOpenGlInit fired
        // before any document was bound (editor starts on welcome screen).
        if (_renderer is null)
        {
            _renderer = new TerrainGlRenderer(World);
            _renderer.Init(gl);
        }

        EnforceTopDown();

        // Avalonia's OpenGlControlBase hands us a framebuffer sized to the
        // control's *physical* pixel area (Bounds × RenderScaling). On a
        // HiDPI monitor passing just Bounds to gl.Viewport renders into
        // the bottom-left corner of the FB and leaves the rest black — but
        // the composited result ends up stretched over the whole control,
        // so cursor positions (reported in DIPs) stop matching world cells.
        // Scale here fixes both rendering coverage and pointer hit tests.
        double rs = Avalonia.Controls.TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = Math.Max(1, (int)(Bounds.Width * rs));
        int h = Math.Max(1, (int)(Bounds.Height * rs));
        float aspect = (float)w / h;

        gl.BindFramebuffer(GL_FRAMEBUFFER, fb);

        float brushRadiusWorld = Tools?.Brush.Radius ?? 1;
        bool brushPaintableNow = _brushVisible && AllowPainting && !CornerToolActive && !DropperToolActive && !PlaceObjectToolActive;

        // Greyscale range: at least [0, 1] in painted units; widen if the
        // world has reached further.
        var (lowN, highN) = World.GetElevationRangeNormalized();
        float scale = Models.Rendering.TerrainMeshBuilder.ElevationScale;
        float greyLow  = Math.Min(0f, lowN)  * scale;
        float greyHigh = Math.Max(1f, highN) * scale;

        var frustumPoints = ComputeFrustumPoints();
        var gpuObjects = ComputeGpuObjects();
        var riverMesh = ComputeRiverMesh();

        _renderer.Render(gl, w, h, Camera.View, Camera.Projection(aspect),
                         _lastBrushCenter, brushRadiusWorld, brushPaintableNow,
                         ShowElevationGreyscale,
                         showCornerMarkers: CornerToolActive && TopDown,
                         selectedCorners: CornerToolActive ? SelectedCorners : null,
                         greyLowY: greyLow, greyHighY: greyHigh,
                         hideUnset: !TopDown,
                         frustumPoints: frustumPoints,
                         objects: gpuObjects,
                         riverMesh: riverMesh,
                         rivers: Rivers);

        if (CaptureRequested)
        {
            CaptureRequested = false;
            var buf = new byte[w * h * 4];
            fixed (byte* p = buf)
                _renderer.Extras.ReadPixels(0, 0, w, h, GL_RGBA, GL_UNSIGNED_BYTE, p);
            LastCaptureRgba = buf;
            LastCaptureWidth = w;
            LastCaptureHeight = h;
            Dispatcher.UIThread.Post(() => CaptureCompleted?.Invoke(),
                DispatcherPriority.Background);
        }

        // Any dependent viewport (e.g. the top-down showing this cam's
        // frustum) needs a frame now that this one moved.
        if (MirrorFramesTo is { } other) other.RequestFrame();
    }

    // Project folder for the loaded scene; icon paths in IconRef.ImagePath
    // are resolved relative to this. MainWindow sets it alongside BindContext.
    public string? ProjectFolderPath { get; set; }

    // Centre a Phase 2a cube 1 world unit above the surface — matches the
    // 2.0 uSize uniform in TerrainGlRenderer.DrawObjects.
    private const float CubeHalfEdge = 1.0f;

    private GpuObjectInstance[]? ComputeGpuObjects()
    {
        if (Objects is null || Objects.Count == 0 || World is null) return null;
        var selected = _boundContext?.SelectedObjectIds;
        var arr = new GpuObjectInstance[Objects.Count];
        for (int i = 0; i < Objects.Count; i++)
        {
            var o = Objects[i];
            float sy = TerrainMeshBuilder.SampleSurfaceY(World, o.CellX, o.CellY);
            var pos = new Vector3(o.CellX + 0.5f, sy + o.YOffsetWorld + CubeHalfEdge, o.CellY + 0.5f);
            bool isSelected = selected?.Contains(o.Id) ?? false;

            var icon = o.IconOverride;
            if (icon is null && Prefabs?.Find(o.PrefabId) is { Icon: { } prefabIcon })
                icon = prefabIcon;
            if (icon is null || string.IsNullOrEmpty(icon.ImagePath))
            {
                arr[i] = new GpuObjectInstance(pos, null, Vector4.Zero, isSelected);
                continue;
            }
            var path = icon.ImagePath;
            if (!System.IO.Path.IsPathRooted(path) && !string.IsNullOrEmpty(ProjectFolderPath))
                path = System.IO.Path.Combine(ProjectFolderPath, path);
            int cols = System.Math.Max(1, icon.GridCols);
            int rows = System.Math.Max(1, icon.GridRows);
            float u0 = (float)icon.CellCol / cols;
            float v0 = (float)icon.CellRow / rows;
            float u1 = (float)(icon.CellCol + 1) / cols;
            float v1 = (float)(icon.CellRow + 1) / rows;
            arr[i] = new GpuObjectInstance(pos, path, new Vector4(u0, v0, u1, v1), isSelected);
        }
        return arr;
    }

    // Build one combined strip mesh for every river in the scene (branches
    // recurse). Each segment [i, i+1] becomes a 4-vertex quad with the
    // perpendicular offset = width/2. Positions sit at the terrain surface
    // plus 0.08 world units so they don't z-fight the ground; Phase 4 will
    // instead push terrain under them.
    private const float RiverLiftY = 0.08f;
    private GpuRiverMesh? ComputeRiverMesh()
    {
        if (Rivers is null || Rivers.Count == 0 || World is null) return null;
        var verts = new System.Collections.Generic.List<float>();
        var idx = new System.Collections.Generic.List<uint>();
        foreach (var r in Rivers) AddRiver(verts, idx, r);
        if (idx.Count == 0) return null;
        return new GpuRiverMesh { Vertices = verts.ToArray(), Indices = idx.ToArray() };
    }

    private void AddRiver(System.Collections.Generic.List<float> verts,
                           System.Collections.Generic.List<uint> idx, River r)
    {
        if (r.Vertices.Count >= 2)
        {
            for (int i = 0; i < r.Vertices.Count - 1; i++)
                AddSegment(verts, idx, r.Vertices[i], r.Vertices[i + 1]);
        }
        foreach (var b in r.Branches) AddRiver(verts, idx, b);
    }

    private void AddSegment(System.Collections.Generic.List<float> verts,
                             System.Collections.Generic.List<uint> idx,
                             RiverVertex a, RiverVertex b)
    {
        if (World is null) return;
        float ax = a.WorldX, az = a.WorldZ;
        float bx = b.WorldX, bz = b.WorldZ;
        float ay = TerrainMeshBuilder.SampleSurfaceY(World,
                        (int)System.Math.Floor(ax), (int)System.Math.Floor(az)) + RiverLiftY;
        float by = TerrainMeshBuilder.SampleSurfaceY(World,
                        (int)System.Math.Floor(bx), (int)System.Math.Floor(bz)) + RiverLiftY;
        float dx = bx - ax, dz = bz - az;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1e-5f) return;
        float nx = -dz / len, nz = dx / len;  // left-hand perpendicular on XZ
        float wa = MathF.Max(0.01f, a.WidthWorld) * 0.5f;
        float wb = MathF.Max(0.01f, b.WidthWorld) * 0.5f;

        uint baseIdx = (uint)(verts.Count / 3);
        // v0 = a + perp*wa (left), v1 = a - perp*wa (right)
        verts.Add(ax + nx * wa); verts.Add(ay); verts.Add(az + nz * wa);
        verts.Add(ax - nx * wa); verts.Add(ay); verts.Add(az - nz * wa);
        verts.Add(bx + nx * wb); verts.Add(by); verts.Add(bz + nz * wb);
        verts.Add(bx - nx * wb); verts.Add(by); verts.Add(bz - nz * wb);
        // two triangles, CCW from above
        idx.Add(baseIdx + 0); idx.Add(baseIdx + 2); idx.Add(baseIdx + 1);
        idx.Add(baseIdx + 1); idx.Add(baseIdx + 2); idx.Add(baseIdx + 3);
    }

    // Projects the source viewport's view frustum onto y=0 and returns
    // [apex, TL, TR, BR, BL] in world coords. Null if there's no source,
    // or if the source is orthographic (frustum isn't meaningful there).
    private Vector3[]? ComputeFrustumPoints()
    {
        if (FrustumSource is not { } src) return null;
        var cam = src.Camera;
        if (cam.IsOrthographic) return null;
        int sw = Math.Max(1, (int)src.Bounds.Width);
        int sh = Math.Max(1, (int)src.Bounds.Height);
        if (sw <= 1 || sh <= 1) return null;
        float srcAspect = (float)sw / sh;

        Vector3 Hit(float sx, float sy)
        {
            var (origin, dir) = cam.ScreenRay(sx, sy, srcAspect);
            float t;
            if (dir.Y < -1e-4f && origin.Y > 0f) t = -origin.Y / dir.Y;
            else t = cam.FarPlane * 0.5f;  // ray climbs — cap so we still show direction
            return origin + dir * t;
        }

        var pts = new Vector3[5];
        pts[0] = cam.Position;
        pts[1] = Hit(-1f,  1f);
        pts[2] = Hit( 1f,  1f);
        pts[3] = Hit( 1f, -1f);
        pts[4] = Hit(-1f, -1f);
        return pts;
    }

    public void RequestFrame()
    {
        Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Background);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        float dt = (float)(now - _lastTick).TotalSeconds;
        _lastTick = now;
        if (dt <= 0 || dt > 0.25f) return;

        if (TopDown)
        {
            // Pan speed scales with ortho scale so the view travels at a
            // consistent screen-fractions-per-second regardless of zoom.
            // ~55% of the half-height per second feels about right.
            float panPerSec = Camera.OrthoHalfHeight * 0.55f;
            if (_held.Contains(Key.LeftShift)) panPerSec *= 2.25f;
            float pan = panPerSec * dt;

            var move = Vector3.Zero;
            if (_held.Contains(Key.W)) move += new Vector3(0, 0, -1);
            if (_held.Contains(Key.S)) move += new Vector3(0, 0, 1);
            if (_held.Contains(Key.A)) move += new Vector3(-1, 0, 0);
            if (_held.Contains(Key.D)) move += new Vector3(1, 0, 0);
            if (move.LengthSquared() > 0.0001f)
            {
                Camera.Move(Vector3.Normalize(move) * pan);
                RequestFrame();
            }
            if (_held.Contains(Key.R))
            {
                Camera.OrthoHalfHeight = MathF.Max(4f, Camera.OrthoHalfHeight * (1f - 1.2f * dt));
                RequestFrame();
            }
            if (_held.Contains(Key.F))
            {
                Camera.OrthoHalfHeight = MathF.Min(400f, Camera.OrthoHalfHeight * (1f + 1.2f * dt));
                RequestFrame();
            }
            return;
        }

        float speed = _held.Contains(Key.LeftShift) ? 25f : 10f;

        // Flat-plane WASD: use the camera forward projected to the XZ plane
        // so pressing W drives horizontally even when the camera is pitched.
        // Camera.Right is already in XZ. R/F stay as world-up/down.
        var flatFwd = new Vector3(Camera.Forward.X, 0, Camera.Forward.Z);
        if (flatFwd.LengthSquared() > 1e-6f) flatFwd = Vector3.Normalize(flatFwd);

        var fmove = Vector3.Zero;
        if (_held.Contains(Key.W)) fmove += flatFwd;
        if (_held.Contains(Key.S)) fmove -= flatFwd;
        if (_held.Contains(Key.A)) fmove -= Camera.Right;
        if (_held.Contains(Key.D)) fmove += Camera.Right;
        if (_held.Contains(Key.R)) fmove += Vector3.UnitY;
        if (_held.Contains(Key.F)) fmove -= Vector3.UnitY;
        if (fmove.LengthSquared() > 0.0001f)
        {
            fmove = Vector3.Normalize(fmove) * speed * dt;
            Camera.Move(fmove);
            RequestFrame();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _brushVisible = false;
        _held.Clear();  // drop sticky keys when the mouse leaves
        RequestFrame();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        HandleWindowPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        HandleWindowPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        HandleWindowPointerReleased(e);
    }

    public void HandleWindowDoubleTapped(Point pos)
    {
        if (!TopDown || !PlaceObjectToolActive) return;
        var hit = PickObjectAt(pos);
        if (hit is not null) ObjectEditRequested?.Invoke(hit.Id);
    }

    protected override void OnDoubleTapped(Avalonia.Input.TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        HandleWindowDoubleTapped(e.GetPosition(this));
    }

    // Public window-level handlers. MainWindow dispatches here because the GL
    // control's own hit-test is unreliable, so we can't count on the protected
    // overrides firing.
    public void HandleWindowPointerPressed(PointerPressedEventArgs e)
    {
        // Clicks are inert while free-camera mode owns the mouse.
        if (_cameraFree) return;

        var pos = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            _rotating = true;
            _rotateLast = pos;
            return;
        }
        if (props.IsLeftButtonPressed && DropperToolActive && TopDown)
        {
            var cell = PickWorldCell(pos);
            if (cell.HasValue) CellSampled?.Invoke(cell.Value.x, cell.Value.y);
            return;
        }
        if (props.IsLeftButtonPressed && PlaceObjectToolActive && TopDown)
        {
            // Defer the decision until release: click → place, drag → select.
            _objectGesturePressed = true;
            _objectGestureDragging = false;
            _objectGestureStart = pos;
            return;
        }
        if (props.IsLeftButtonPressed && RiverToolActive && TopDown)
        {
            // Fine-grained world point so sub-cell insets land where the
            // user actually clicked.
            var wp = PickWorldPoint(pos);
            if (wp is { } p)
            {
                var hit = PickRiverVertex(p.x, p.y);
                if (hit.HasValue)
                    RiverVertexSelectRequested?.Invoke(hit.Value.riverId, hit.Value.index);
                else
                    RiverVertexAddRequested?.Invoke(p.x, p.y);
            }
            return;
        }
        if (props.IsLeftButtonPressed && CornerToolActive && TopDown)
        {
            _boxSelecting = true;
            _shiftAtPressStart = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _boxStart = pos;
            BoxSelectRect = new Avalonia.Rect(pos, pos);
            BoxSelectVisualChanged?.Invoke();
            return;
        }
        if (props.IsLeftButtonPressed && AllowPainting && Tools is not null)
        {
            var cell = PickWorldCell(pos);
            if (cell.HasValue)
            {
                _painting = true;
                Tools.Paint.BeginStroke(cell.Value.x, cell.Value.y);
                _lastBrushCenter = new Vector3(cell.Value.x, 0, cell.Value.y);
                RequestFrame();
            }
        }
    }

    public void HandleWindowPointerMoved(PointerEventArgs e)
    {
        var pos = e.GetPosition(this);

        if (_cameraFree)
        {
            if (_cameraFreeFirstMove)
            {
                _rotateLast = pos;
                _cameraFreeFirstMove = false;
            }
            else
            {
                var d = pos - _rotateLast;
                _rotateLast = pos;
                if (TopDown)
                {
                    float h = Math.Max(1, (int)Bounds.Height);
                    float worldPerPx = (2f * Camera.OrthoHalfHeight) / h;
                    Camera.Position += new Vector3(-(float)d.X * worldPerPx, 0, -(float)d.Y * worldPerPx);
                }
                else
                {
                    Camera.Rotate(-(float)d.X * 0.005f, -(float)d.Y * 0.005f);
                }
                RequestFrame();
            }
            return;
        }

        if (_boxSelecting)
        {
            double x0 = Math.Min(_boxStart.X, pos.X), y0 = Math.Min(_boxStart.Y, pos.Y);
            double x1 = Math.Max(_boxStart.X, pos.X), y1 = Math.Max(_boxStart.Y, pos.Y);
            BoxSelectRect = new Avalonia.Rect(x0, y0, x1 - x0, y1 - y0);
            BoxSelectVisualChanged?.Invoke();
            return;
        }

        if (_objectGesturePressed)
        {
            var d = pos - _objectGestureStart;
            if (!_objectGestureDragging
                && (Math.Abs(d.X) > ObjectDragThresholdPx || Math.Abs(d.Y) > ObjectDragThresholdPx))
            {
                _objectGestureDragging = true;
            }
            if (_objectGestureDragging)
            {
                double x0 = Math.Min(_objectGestureStart.X, pos.X);
                double y0 = Math.Min(_objectGestureStart.Y, pos.Y);
                double x1 = Math.Max(_objectGestureStart.X, pos.X);
                double y1 = Math.Max(_objectGestureStart.Y, pos.Y);
                BoxSelectRect = new Avalonia.Rect(x0, y0, x1 - x0, y1 - y0);
                BoxSelectVisualChanged?.Invoke();
            }
            return;
        }

        if (_rotating)
        {
            var d = pos - _rotateLast;
            _rotateLast = pos;
            if (TopDown)
            {
                float h = Math.Max(1, (int)Bounds.Height);
                float worldPerPx = (2f * Camera.OrthoHalfHeight) / h;
                Camera.Position += new Vector3(-(float)d.X * worldPerPx, 0, -(float)d.Y * worldPerPx);
            }
            else
            {
                Camera.Rotate(-(float)d.X * 0.005f, -(float)d.Y * 0.005f);
            }
            RequestFrame();
            return;
        }

        if (AllowPainting)
        {
            var cell = PickWorldCell(pos);
            if (cell.HasValue)
            {
                _lastBrushCenter = new Vector3(cell.Value.x, 0, cell.Value.y);
                _brushVisible = true;
                if (_painting && Tools is not null)
                    Tools.Paint.ContinueStroke(cell.Value.x, cell.Value.y);
                RequestFrame();
            }
            else { _brushVisible = false; RequestFrame(); }
        }
    }

    public void HandleWindowPointerReleased(PointerReleasedEventArgs e)
    {
        if (_rotating && e.InitialPressMouseButton == MouseButton.Right)
            _rotating = false;
        if (_painting && e.InitialPressMouseButton == MouseButton.Left)
        {
            _painting = false;
            Tools?.Paint.EndStroke();
            RequestFrame();
        }
        if (_boxSelecting && e.InitialPressMouseButton == MouseButton.Left)
        {
            _boxSelecting = false;
            CommitBoxSelection();
            BoxSelectRect = null;
            BoxSelectVisualChanged?.Invoke();
            RequestFrame();
        }
        if (_objectGesturePressed && e.InitialPressMouseButton == MouseButton.Left)
        {
            _objectGesturePressed = false;
            if (_objectGestureDragging)
            {
                CommitObjectBoxSelection();
            }
            else
            {
                // Click (not drag): hit existing object → single-select;
                // empty space → drop a new one.
                var hit = PickObjectAt(_objectGestureStart);
                if (hit is not null)
                {
                    ObjectSingleSelectRequested?.Invoke(hit.Id);
                }
                else
                {
                    var cell = PickWorldCell(_objectGestureStart);
                    if (cell.HasValue) ObjectPlaceRequested?.Invoke(cell.Value.x, cell.Value.y);
                }
            }
            _objectGestureDragging = false;
            BoxSelectRect = null;
            BoxSelectVisualChanged?.Invoke();
            RequestFrame();
        }
    }

    // Translate the current pixel-space BoxSelectRect into a world-space
    // XZ rectangle and raise ObjectBoxSelectRequested. Mirrors the math
    // in CommitBoxSelection but for objects instead of chunk corners.
    private void CommitObjectBoxSelection()
    {
        if (BoxSelectRect is not { } r) return;
        int w = Math.Max(1, (int)Bounds.Width);
        int h = Math.Max(1, (int)Bounds.Height);
        float aspect = (float)w / h;
        float halfH = Camera.OrthoHalfHeight;
        float halfW = halfH * aspect;

        double WorldAtX(double sx) => Camera.Position.X + (sx / w * 2.0 - 1.0) * halfW;
        double WorldAtZ(double sy) => Camera.Position.Z + (1.0 - sy / h * 2.0) * -halfH;

        double wx0 = WorldAtX(r.Left),  wx1 = WorldAtX(r.Right);
        double wz0 = WorldAtZ(r.Top),   wz1 = WorldAtZ(r.Bottom);
        if (wx0 > wx1) (wx0, wx1) = (wx1, wx0);
        if (wz0 > wz1) (wz0, wz1) = (wz1, wz0);
        ObjectBoxSelectRequested?.Invoke((float)wx0, (float)wz0, (float)wx1, (float)wz1);
    }

    private void CommitBoxSelection()
    {
        if (BoxSelectRect is not { } r) return;
        if (World is null) return;

        int w = Math.Max(1, (int)Bounds.Width);
        int h = Math.Max(1, (int)Bounds.Height);
        float aspect = (float)w / h;
        float halfH = Camera.OrthoHalfHeight;
        float halfW = halfH * aspect;

        double WorldAtX(double sx) => Camera.Position.X + (sx / w * 2.0 - 1.0) * halfW;
        double WorldAtZ(double sy) => Camera.Position.Z + (1.0 - sy / h * 2.0) * -halfH;

        double wx0 = WorldAtX(r.Left),  wx1 = WorldAtX(r.Right);
        double wz0 = WorldAtZ(r.Top),   wz1 = WorldAtZ(r.Bottom);
        if (wx0 > wx1) (wx0, wx1) = (wx1, wx0);
        if (wz0 > wz1) (wz0, wz1) = (wz1, wz0);

        int size = Chunk.Size;
        var cornersInBox = new List<(int cx, int cy)>();
        foreach (var (cx, cy) in World.EnumerateChunkCorners())
        {
            double wx = cx * (double)size;
            double wz = cy * (double)size;
            if (wx >= wx0 && wx <= wx1 && wz >= wz0 && wz <= wz1)
                cornersInBox.Add((cx, cy));
        }

        bool isClickWithoutDrag = r.Width < 3 && r.Height < 3;

        if (_shiftAtPressStart)
        {
            // Shift + box: toggle each corner in the box. Points outside the
            // box are left alone.
            foreach (var c in cornersInBox)
                if (!SelectedCorners.Add(c)) SelectedCorners.Remove(c);
        }
        else if (isClickWithoutDrag && cornersInBox.Count == 0)
        {
            // Click on empty space clears the whole selection.
            SelectedCorners.Clear();
        }
        else
        {
            // Normal drag — additive.
            foreach (var c in cornersInBox) SelectedCorners.Add(c);
        }

        SelectionChanged?.Invoke();
    }

    public void ClearSelectedCorners()
    {
        if (SelectedCorners.Count == 0) return;
        SelectedCorners.Clear();
        SelectionChanged?.Invoke();
        RequestFrame();
    }

    public void HandleWindowPointerWheel(PointerWheelEventArgs e)
    {
        if (TopDown)
        {
            float factor = e.Delta.Y > 0 ? 0.85f : 1.18f;
            Camera.OrthoHalfHeight = Math.Clamp(Camera.OrthoHalfHeight * factor, 4f, 400f);
        }
        else
        {
            Camera.Move(Camera.Forward * (float)e.Delta.Y * 4f);
        }
        RequestFrame();
    }

    private (int x, int y)? PickWorldCell(Point screenPt)
    {
        var p = PickWorldPoint(screenPt);
        if (p is null) return null;
        return ((int)MathF.Floor(p.Value.x), (int)MathF.Floor(p.Value.y));
    }

    // Floating-point world XZ under the pointer. Used for picking which
    // existing object a click sits on top of (hit-test radius is in world
    // units, independent of zoom).
    public (float x, float y)? PickWorldPoint(Point screenPt)
    {
        int w = Math.Max(1, (int)Bounds.Width);
        int h = Math.Max(1, (int)Bounds.Height);
        float sx = ((float)screenPt.X / w) * 2f - 1f;
        float sy = 1f - ((float)screenPt.Y / h) * 2f;

        if (TopDown)
        {
            float aspect = (float)w / h;
            float hx = sx * Camera.OrthoHalfHeight * aspect;
            float hz = -sy * Camera.OrthoHalfHeight;   // screen up = -Z
            return (Camera.Position.X + hx, Camera.Position.Z + hz);
        }

        var (origin, dir) = Camera.ScreenRay(sx, sy, (float)w / h);
        if (MathF.Abs(dir.Y) < 1e-5f) return null;
        float t = -origin.Y / dir.Y;
        if (t < 0) return null;
        var hit = origin + dir * t;
        return (hit.X, hit.Z);
    }

    // Nearest river vertex within RiverPickRadius world units of (wx, wz).
    // Walks branches recursively; returns the first hit (any river).
    private const float RiverPickRadius = 2.0f;
    private (string riverId, int index)? PickRiverVertex(float wx, float wz)
    {
        if (Rivers is null) return null;
        (string, int)? best = null;
        float bestD2 = RiverPickRadius * RiverPickRadius;
        foreach (var r in Rivers)
        {
            var hit = PickRiverVertexIn(r, wx, wz, ref bestD2);
            if (hit.HasValue) best = hit;
        }
        return best;
    }

    private static (string, int)? PickRiverVertexIn(River r, float wx, float wz, ref float bestD2)
    {
        (string, int)? best = null;
        for (int i = 0; i < r.Vertices.Count; i++)
        {
            var v = r.Vertices[i];
            float dx = v.WorldX - wx, dz = v.WorldZ - wz;
            float d2 = dx * dx + dz * dz;
            if (d2 <= bestD2) { bestD2 = d2; best = (r.Id, i); }
        }
        foreach (var b in r.Branches)
        {
            var h = PickRiverVertexIn(b, wx, wz, ref bestD2);
            if (h.HasValue) best = h;
        }
        return best;
    }

    // Nearest instance (by XZ distance) within ObjectPickRadius world units
    // of the pointer. Null → nothing close enough.
    private const float ObjectPickRadius = 1.5f;
    private ObjectInstance? PickObjectAt(Point screenPt)
    {
        if (Objects is null || Objects.Count == 0) return null;
        if (PickWorldPoint(screenPt) is not { } wp) return null;
        ObjectInstance? best = null;
        float bestD2 = ObjectPickRadius * ObjectPickRadius;
        foreach (var o in Objects)
        {
            float dx = o.CellX + 0.5f - wp.x;
            float dy = o.CellY + 0.5f - wp.y;
            float d2 = dx * dx + dy * dy;
            if (d2 <= bestD2) { bestD2 = d2; best = o; }
        }
        return best;
    }
}
