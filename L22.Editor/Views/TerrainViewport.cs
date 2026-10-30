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
    // in both viewports (icons come in a later phase).
    public System.Collections.Generic.IReadOnlyList<ObjectInstance>? Objects { get; set; }
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
        if (_boundContext is not null) _boundContext.ObjectsChanged -= OnObjectsChanged;
        BindWorld(ctx.World, allowTools ? ctx.Tools : null);
        Objects = ctx.Objects;
        _boundContext = ctx;
        ctx.ObjectsChanged += OnObjectsChanged;
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

        int w = Math.Max(1, (int)Bounds.Width);
        int h = Math.Max(1, (int)Bounds.Height);
        float aspect = (float)w / h;

        gl.BindFramebuffer(GL_FRAMEBUFFER, fb);

        float brushRadiusWorld = Tools?.Brush.Radius ?? 1;
        bool brushPaintableNow = _brushVisible && AllowPainting && !CornerToolActive && !DropperToolActive;

        // Greyscale range: at least [0, 1] in painted units; widen if the
        // world has reached further.
        var (lowN, highN) = World.GetElevationRangeNormalized();
        float scale = Models.Rendering.TerrainMeshBuilder.ElevationScale;
        float greyLow  = Math.Min(0f, lowN)  * scale;
        float greyHigh = Math.Max(1f, highN) * scale;

        var frustumPoints = ComputeFrustumPoints();
        var objectCubes = ComputeObjectCubeCenters();

        _renderer.Render(gl, w, h, Camera.View, Camera.Projection(aspect),
                         _lastBrushCenter, brushRadiusWorld, brushPaintableNow,
                         ShowElevationGreyscale,
                         showCornerMarkers: CornerToolActive && TopDown,
                         selectedCorners: CornerToolActive ? SelectedCorners : null,
                         greyLowY: greyLow, greyHighY: greyHigh,
                         hideUnset: !TopDown,
                         frustumPoints: frustumPoints,
                         objectCubes: objectCubes);

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

    // Pink-cube fallback positions for Phase 2a. Icon-textured billboards
    // come in Phase 2b. Y is terrain-surface + YOffsetWorld + half the
    // cube's edge so its bottom sits on the ground. CubeHalfEdge must stay
    // in sync with uSize in TerrainGlRenderer.DrawObjectCubes.
    private const float CubeHalfEdge = 1.0f;  // uSize=2.0 → half-edge 1
    private Vector3[]? ComputeObjectCubeCenters()
    {
        if (Objects is null || Objects.Count == 0 || World is null) return null;
        var pts = new Vector3[Objects.Count];
        for (int i = 0; i < Objects.Count; i++)
        {
            var o = Objects[i];
            float sy = TerrainMeshBuilder.SampleSurfaceY(World, o.CellX, o.CellY);
            pts[i] = new Vector3(o.CellX + 0.5f, sy + o.YOffsetWorld + CubeHalfEdge, o.CellY + 0.5f);
        }
        return pts;
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
        int w = Math.Max(1, (int)Bounds.Width);
        int h = Math.Max(1, (int)Bounds.Height);
        float sx = ((float)screenPt.X / w) * 2f - 1f;
        float sy = 1f - ((float)screenPt.Y / h) * 2f;

        if (TopDown)
        {
            // Direct XZ mapping from NDC; no ray-plane needed.
            float aspect = (float)w / h;
            float hx = sx * Camera.OrthoHalfHeight * aspect;
            float hz = -sy * Camera.OrthoHalfHeight;   // screen up = -Z
            float wx = Camera.Position.X + hx;
            float wz = Camera.Position.Z + hz;
            return ((int)MathF.Floor(wx), (int)MathF.Floor(wz));
        }

        var (origin, dir) = Camera.ScreenRay(sx, sy, (float)w / h);
        if (MathF.Abs(dir.Y) < 1e-5f) return null;
        float t = -origin.Y / dir.Y;
        if (t < 0) return null;
        var hit = origin + dir * t;
        return ((int)MathF.Floor(hit.X), (int)MathF.Floor(hit.Z));
    }
}
