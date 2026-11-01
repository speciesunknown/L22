using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using L22.Editor.Models;
using L22.Editor.Models.Commands;
using L22.Editor.Models.World;
using L22.Editor.ViewModels;
using L22.Editor.Views;

namespace L22.Editor;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; private set; } = null!;

    // Slider inline-edit — hover a slider, start typing a number, the slider's
    // value label flips to a TextBox you can type into.
    private sealed record SliderBinding(Slider Slider, TextBlock Label, TextBox Editor, Action<double> Apply, Func<double, string> Format);
    private readonly Dictionary<Slider, SliderBinding> _sliderBindings = new();
    private readonly Dictionary<TextBox, SliderBinding> _editorBindings = new();
    private Slider? _hoveredSlider;

    private TerrainViewport? _lastHoveredViewport;
    // Viewport the pointer is currently inside, if any. Transitions away
    // from a viewport trigger a key-reset so held-down WASD doesn't keep
    // moving the old view when the mouse enters something else.
    private TerrainViewport? _currentHoveredViewport;

    // Rasterised cursors — built in ctor from the SVG-path geometries so the
    // same shape appears in the toolbar and under the cursor.
    private Cursor _moveCursor = Cursor.Default;
    private Cursor _boxSelectCursor = Cursor.Default;
    private Cursor _dropperCursor = Cursor.Default;

    public MainWindow()
    {
        InitializeComponent();

        ViewModel = new MainViewModel();

        // Verify mode seeds a document via Program.Main so the capture has
        // something to paint on; normal startup opens empty.
        if (EditorContext.Current is not null)
        {
            var seed = EditorContext.Current;
            var seedDoc = new OpenDocument(seed, DocumentKind.Scene, seed.World.FileId);
            ViewModel.Documents.Add(seedDoc);
            ViewModel.Active = seedDoc;
        }
        else if (!string.IsNullOrEmpty(Program.Args.OpenPath))
        {
            try { ViewModel.OpenFromFile(Program.Args.OpenPath); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[--open] failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Tabs.ItemsSource = ViewModel.Documents;
        Tabs.SelectedItem = ViewModel.Active;
        RecentList.ItemsSource = ViewModel.RecentScenes;

        EditViewport.AllowPainting = true;
        EditViewport.TopDown = true;
        FreeViewport.AllowPainting = false;

        FreeViewport.Camera.Position = new System.Numerics.Vector3(140, 55, 160);
        FreeViewport.Camera.Yaw = -0.65f;
        FreeViewport.Camera.Pitch = -0.45f;

        ViewModel.Documents.CollectionChanged += (_, _) => RefreshStartState();
        RefreshStartState();
        if (ViewModel.Active is not null) BindViewportsToActive();

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent,   OnWindowKeyUp,   RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent,        OnWindowPointerMoved,    RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent,      OnWindowPointerPressed,  RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent,     OnWindowPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, OnWindowPointerWheel,    RoutingStrategies.Tunnel, handledEventsToo: true);

        if (ViewModel.Active is not null)
        {
            RefreshSceneGraph();
            RefreshBrushBindings();
            UpdateLayerUi();
        }

        EditViewport.CameraFreeChanged += () => RefreshCamToggle(EditCamToggle, EditCamIcon, EditViewport.CameraFree);
        FreeViewport.CameraFreeChanged += () => RefreshCamToggle(FreeCamToggle, FreeCamIcon, FreeViewport.CameraFree);

        EditViewport.BoxSelectVisualChanged += RefreshRubberBand;
        EditViewport.SelectionChanged += UpdateCornerPanel;
        EditViewport.CellSampled += OnCellSampled;
        EditViewport.ObjectPlaceRequested += OnObjectPlaceRequested;
        EditViewport.ObjectBoxSelectRequested += OnObjectBoxSelectRequested;
        EditViewport.ObjectSingleSelectRequested += OnObjectSingleSelectRequested;
        EditViewport.ObjectEditRequested += OnObjectEditFromViewport;
        EditViewport.RiverVertexAddRequested += OnRiverVertexAddRequested;
        EditViewport.RiverVertexSelectRequested += OnRiverVertexSelectRequested;
        EditViewport.RiverHandleDragPreview += OnRiverHandleDragPreview;
        EditViewport.RiverHandleDragRelease += OnRiverHandleDragRelease;
        SlRiverWidth.ValueChanged += OnRiverWidthSliderChanged;

        RegisterSliderBinding(SlRadius,    LblRadius,    TxtRadius,    v => { if (ViewModel.Brush is { } b) b.Radius = (int)Math.Round(v); }, v => ((int)Math.Round(v)).ToString());
        RegisterSliderBinding(SlStrength,  LblStrength,  TxtStrength,  v => { if (ViewModel.Brush is { } b) b.Strength = (float)v; },        v => v.ToString("0.00"));
        RegisterSliderBinding(SlElevation, LblElevation, TxtElevation, v => { if (ViewModel.Brush is { } b) b.ElevationValue = (float)v; },  v => v.ToString("0.00"));

        // Window loses focus (Alt-Tab, click another app) → no key-up events
        // will arrive. Drop any still-held keys so the camera doesn't keep
        // moving when we come back. Also forget which viewport we were
        // over so the next pointer-move treats re-entry as a fresh hover.
        Deactivated += (_, _) =>
        {
            EditViewport.ClearHeldKeys();
            FreeViewport.ClearHeldKeys();
            _currentHoveredViewport = null;
        };

        // The top-down viewport renders the 3D camera's frustum as GL
        // geometry on the ground plane. We tell it which camera to project
        // and ask the 3D viewport to kick a frame on its sibling after each
        // of its own renders so the frustum tracks movement.
        EditViewport.FrustumSource = FreeViewport;
        FreeViewport.MirrorFramesTo = EditViewport;

        // River overlay follows the top-down camera; refresh on a cheap
        // dispatcher tick so pan / zoom / WASD moves don't leave it stale.
        var riverTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(60),
            DispatcherPriority.Background, (_, _) => RefreshRiverOverlay());
        riverTimer.Start();

        ViewModel.PropertyChanged += (_, _) => UpdateWindowTitle();
        UpdateWindowTitle();

        // Restore BEFORE the window reaches the show pipeline — in Avalonia
        // 11 the "last-normal restore bounds" are captured from the XAML
        // size on first layout, so setting Width / Height / Position in the
        // Opened handler is too late for an un-maximise to land where the
        // user last had it. WindowStartupLocation=Manual stops the window
        // manager overriding the Position we set here.
        RestoreWindowStateFromDisk();
        Closing += OnWindowClosingPersistState;
    }

    private void RestoreWindowStateFromDisk()
    {
        var kv = Models.WindowStateStore.LoadAll();
        if (kv.TryGetValue(StateKeyWidth, out var ws) && double.TryParse(ws, System.Globalization.CultureInfo.InvariantCulture, out var w) && w > 100)
            Width = w;
        if (kv.TryGetValue(StateKeyHeight, out var hs) && double.TryParse(hs, System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 100)
            Height = h;
        if (kv.TryGetValue(StateKeyPosX, out var xs) && int.TryParse(xs, out var x)
            && kv.TryGetValue(StateKeyPosY, out var ys) && int.TryParse(ys, out var y))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }
        if (kv.TryGetValue(StateKeyState, out var st) && Enum.TryParse<WindowState>(st, out var ws2)
            && (ws2 == WindowState.Maximized || ws2 == WindowState.Normal))
        {
            WindowState = ws2;
        }
    }

    private const string StateKeyPosX   = "window.posX";
    private const string StateKeyPosY   = "window.posY";
    private const string StateKeyWidth  = "window.width";
    private const string StateKeyHeight = "window.height";
    private const string StateKeyState  = "window.state";

    private void OnWindowClosingPersistState(object? sender, Avalonia.Controls.WindowClosingEventArgs e)
    {
        // Capture *restoreBounds* when maximised: Position / Width / Height
        // report the maximised cover, which would make the next session's
        // Normal state cover the whole screen.
        bool maximised = WindowState == WindowState.Maximized;
        var pos = Position;
        double saveW = Width;
        double saveH = Height;
        if (maximised)
        {
            // Avalonia doesn't surface RestoreBounds directly; fall back to
            // whatever we had saved last time by not overwriting the X/Y/W/H
            // keys. The State key is enough to reopen maximised.
            Models.WindowStateStore.Save(new Dictionary<string, string>
            {
                [StateKeyState] = WindowState.ToString(),
            });
            return;
        }
        Models.WindowStateStore.Save(new Dictionary<string, string>
        {
            [StateKeyPosX]   = pos.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [StateKeyPosY]   = pos.Y.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [StateKeyWidth]  = saveW.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [StateKeyHeight] = saveH.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [StateKeyState]  = WindowState.ToString(),
        });
    }

    private void BuildToolCursors()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("USERPROFILE") ?? ".",
            ".claude", "tmp", "l22");
        System.IO.Directory.CreateDirectory(dir);
        var logPath = System.IO.Path.Combine(dir, "cursor_diag.log");
        IconCursorFactory.DebugDumpDir = dir;

        void Log(string s) => System.IO.File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\n");

        try { System.IO.File.WriteAllText(logPath, ""); } catch { }
        Log("BuildToolCursors starting");

        _moveCursor      = TryMakeCursor("IconMove",      new PixelPoint(16, 16), "move",      StandardCursorType.SizeAll, Log);
        _boxSelectCursor = TryMakeCursor("IconBoxSelect", new PixelPoint(4, 4),   "boxselect", StandardCursorType.Cross,   Log);
        _dropperCursor   = TryMakeCursor("IconDropper",   new PixelPoint(4, 28),  "dropper",   StandardCursorType.Hand,    Log);

        Log($"done. moveCursor={_moveCursor}, boxSelect={_boxSelectCursor}, dropper={_dropperCursor}");
    }

    private static Cursor TryMakeCursor(string resourceKey, PixelPoint hotspot, string debugName, StandardCursorType fallback, Action<string> log)
    {
        try
        {
            if (Application.Current?.Resources is not { } res)
            {
                log($"{debugName}: Application.Current.Resources is null");
                return new Cursor(fallback);
            }
            if (!res.TryGetResource(resourceKey, null, out var obj))
            {
                log($"{debugName}: resource '{resourceKey}' missing");
                return new Cursor(fallback);
            }
            if (obj is not Geometry geo)
            {
                log($"{debugName}: resource '{resourceKey}' is {obj?.GetType().FullName ?? "null"}, not Geometry");
                return new Cursor(fallback);
            }
            log($"{debugName}: geometry OK ({geo.GetType().Name})");
            var c = IconCursorFactory.FromGeometry(geo, hotspot, debugName: debugName);
            log($"{debugName}: cursor built OK");
            return c;
        }
        catch (Exception ex)
        {
            log($"{debugName} FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return new Cursor(fallback);
        }
    }


    private void RefreshRubberBand()
    {
        if (EditViewport.BoxSelectRect is { } r)
        {
            RubberBand.IsVisible = true;
            Avalonia.Controls.Canvas.SetLeft(RubberBand, r.X);
            Avalonia.Controls.Canvas.SetTop(RubberBand, r.Y);
            RubberBand.Width = r.Width;
            RubberBand.Height = r.Height;
        }
        else
        {
            RubberBand.IsVisible = false;
        }
    }

    private void RefreshStartState()
    {
        bool hasDoc = ViewModel.Documents.Count > 0;
        WelcomePanel.IsVisible = !hasDoc;
        EditorArea.IsVisible = hasDoc;
        LblNoRecent.IsVisible = ViewModel.RecentScenes.Count == 0;
    }

    private void OnRecentActivated(object? s, Avalonia.Input.TappedEventArgs e)
    {
        // Persistence lands in plan 002; nothing to open yet.
    }

    // ---- document / tab plumbing --------------------------------------------------

    private void BindViewportsToActive()
    {
        var active = ViewModel.Active;
        if (active is null) return;
        EditViewport.BindContext(active.Context);
        FreeViewport.BindContext(active.Context, allowTools: false);
        EditViewport.ProjectFolderPath = active.FilePath;
        FreeViewport.ProjectFolderPath = active.FilePath;
    }

    private void OnTabSelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        // Save the outgoing tab's camera so switching back restores it.
        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is OpenDocument prev)
            SnapshotCamerasInto(prev);

        if (Tabs.SelectedItem is OpenDocument doc)
        {
            ViewModel.Active = doc;
            BindViewportsToActive();
            RestoreCamerasFrom(doc);
            RefreshSceneGraph();
            RefreshBrushBindings();
            UpdateLayerUi();
            LblFileId.Text = $"File ID: {doc.Context.World.FileId}";
            EditViewport.RequestFrame();
            FreeViewport.RequestFrame();
        }
    }

    private void OnNewScene(object? s, RoutedEventArgs e)   => CreateAndSelect(DocumentKind.Scene);
    private void OnNewPrefab(object? s, RoutedEventArgs e)  => CreateAndSelect(DocumentKind.Prefab);

    private async void OnOpenFile(object? s, RoutedEventArgs e)
    {
        var sp = StorageProvider;
        if (sp is null) return;
        var picked = await sp.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Open L22 project",
            AllowMultiple = false,
            FileTypeFilter = new[] { L22ProjFileType, AllFilesFileType },
        });
        if (picked is null || picked.Count == 0) return;
        var path = picked[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        if (ViewModel.Active is { } prev) SnapshotCamerasInto(prev);
        try
        {
            var doc = ViewModel.OpenFromFile(path);
            Tabs.SelectedItem = doc;
            BindViewportsToActive();
            RestoreCamerasFrom(doc);
            RefreshSceneGraph();
            RefreshBrushBindings();
            UpdateLayerUi();
            LblFileId.Text = $"File ID: {doc.Context.World.FileId}";
            UpdateWindowTitle();
            EditViewport.RequestFrame();
            FreeViewport.RequestFrame();
        }
        catch (Exception ex)
        {
            await ShowErrorDialog("Open failed", ex.Message);
        }
    }

    private async void OnSave(object? s, RoutedEventArgs e)
    {
        if (ViewModel.Active is not { } doc) return;
        if (string.IsNullOrEmpty(doc.FilePath))
        {
            await SaveAs(doc);
            return;
        }
        try
        {
            ViewModel.SaveActive(doc.FilePath!);
            UpdateWindowTitle();
        }
        catch (Exception ex)
        {
            await ShowErrorDialog("Save failed", ex.Message);
        }
    }

    private async void OnSaveAs(object? s, RoutedEventArgs e)
    {
        if (ViewModel.Active is { } doc) await SaveAs(doc);
    }

    private async Task SaveAs(OpenDocument doc)
    {
        var sp = StorageProvider;
        if (sp is null) return;
        var picked = await sp.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save L22 project",
            SuggestedFileName = doc.Name,
            DefaultExtension = "l22proj",
            FileTypeChoices = new[] { L22ProjFileType },
        });
        var path = picked?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            ViewModel.SaveActive(path!);
            UpdateWindowTitle();
            Tabs.SelectedItem = doc;  // refresh the tab label (name may have changed)
        }
        catch (Exception ex)
        {
            await ShowErrorDialog("Save failed", ex.Message);
        }
    }

    private static readonly Avalonia.Platform.Storage.FilePickerFileType L22ProjFileType =
        new("L22 project") { Patterns = new[] { "*.l22proj" } };
    private static readonly Avalonia.Platform.Storage.FilePickerFileType AllFilesFileType =
        new("All files") { Patterns = new[] { "*" } };

    private async Task ShowErrorDialog(string title, string message)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right },
                },
            },
        };
        if (dlg.Content is StackPanel sp && sp.Children[^1] is Button b)
            b.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
    }

    private void UpdateWindowTitle()
    {
        var doc = ViewModel.Active;
        if (doc is null) { Title = "L22 World Editor"; return; }
        string star = doc.Context.History.Dirty ? "*" : "";
        string name = doc.Name;
        Title = $"L22 World Editor — {star}{name}";
    }

    private void OnCloseTab(object? s, RoutedEventArgs e)
    {
        if (ViewModel.Active is { } doc)
        {
            SnapshotCamerasInto(doc);
            ViewModel.CloseDocument(doc);
            Tabs.SelectedItem = ViewModel.Active;
            // Zero tabs → welcome screen reappears via RefreshStartState.
        }
    }

    private void SnapshotCamerasInto(OpenDocument doc)
    {
        doc.EditCamera = CameraSnapshot.From(EditViewport.Camera);
        doc.FreeCamera = CameraSnapshot.From(FreeViewport.Camera);
    }

    private void RestoreCamerasFrom(OpenDocument doc)
    {
        if (doc.EditCamera is { } ec) ec.ApplyTo(EditViewport.Camera);
        else EditViewport.ConfigureTopDown();

        if (doc.FreeCamera is { } fc) fc.ApplyTo(FreeViewport.Camera);
        else
        {
            FreeViewport.Camera.Position = new System.Numerics.Vector3(140, 55, 160);
            FreeViewport.Camera.Yaw = -0.65f;
            FreeViewport.Camera.Pitch = -0.45f;
        }
    }

    private void CreateAndSelect(DocumentKind kind)
    {
        // Save the current tab's camera before swapping away from it.
        if (ViewModel.Active is { } prev) SnapshotCamerasInto(prev);

        var baseName = kind == DocumentKind.Prefab ? "prefab" : "scene";
        int n = 1;
        string name;
        do { name = $"{baseName}-{n++}"; }
        while (NameInUse(name));
        var doc = ViewModel.NewDocument(kind, name);
        Tabs.SelectedItem = doc;
        // Avalonia's TabControl sometimes swallows the first SelectionChanged
        // when the control is just becoming visible, so bind the viewports
        // explicitly here as well.
        BindViewportsToActive();
        RestoreCamerasFrom(doc);
        RefreshSceneGraph();
        RefreshBrushBindings();
        UpdateLayerUi();
        LblFileId.Text = $"File ID: {doc.Context.World.FileId}";
    }

    private bool NameInUse(string name)
    {
        foreach (var d in ViewModel.Documents)
            if (d.Name == name) return true;
        return false;
    }

    // ---- right panel bindings ------------------------------------------------------

    private void RefreshBrushBindings()
    {
        var brush = ViewModel.Brush;
        if (brush is null) return;
        if (SlRadius     is not null) SlRadius.Value     = brush.Radius;
        if (SlStrength   is not null) SlStrength.Value   = brush.Strength;
        if (SlElevation  is not null) SlElevation.Value  = brush.ElevationValue;
        if (LblRadius    is not null) LblRadius.Text     = brush.Radius.ToString();
        if (LblStrength  is not null) LblStrength.Text   = brush.Strength.ToString("0.00");
        if (LblElevation is not null) LblElevation.Text  = brush.ElevationValue.ToString("0.00");

        brush.PropertyChanged -= OnBrushPropertyChanged;
        brush.PropertyChanged += OnBrushPropertyChanged;
    }

    private void OnBrushPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e) => UpdateLayerUi();

    private void RefreshSceneGraph()
    {
        var active = ViewModel.Active;
        if (active is null) { SceneGraph.ItemsSource = null; return; }
        var root = new TreeViewItem
        {
            Header = $"{active.TabLabel}  ({active.Kind})",
            IsExpanded = true,
        };
        SceneGraph.ItemsSource = new[] { root };
        BindObjectsPanel(active.Context);
    }

    private EditorContext? _objectsBoundContext;
    private void BindObjectsPanel(EditorContext ctx)
    {
        if (_objectsBoundContext is not null)
        {
            _objectsBoundContext.ObjectsChanged -= OnBoundObjectsChanged;
            _objectsBoundContext.Prefabs.PrefabsChanged -= RefreshPrefabList;
            _objectsBoundContext.RiversChanged -= OnBoundRiversChanged;
        }
        _objectsBoundContext = ctx;
        ctx.ObjectsChanged += OnBoundObjectsChanged;
        ctx.Prefabs.PrefabsChanged += RefreshPrefabList;
        ctx.RiversChanged += OnBoundRiversChanged;
        RefreshObjectList();
        RefreshPrefabList();
        OnBoundRiversChanged();
    }

    private void OnBoundRiversChanged()
    {
        RefreshRiverList();
        RefreshRiverOverlay();
        RefreshRiverVertexPanel();
    }

    private string? _activeRiverId;

    private void RefreshRiverList()
    {
        if (RiverList is null) return;
        var ctx = ViewModel.Active?.Context;
        if (ctx is null)
        {
            RiverList.ItemsSource = null;
            if (LblRiverCount is not null) LblRiverCount.Text = "0 rivers";
            return;
        }
        var labels = new List<string>(ctx.Rivers.Count);
        foreach (var r in ctx.Rivers)
        {
            string name = string.IsNullOrEmpty(r.Name) ? "(unnamed)" : r.Name;
            string marker = r.Id == _activeRiverId ? "● " : "  ";
            labels.Add($"{marker}{name}  ·  {r.Vertices.Count} vtx");
        }
        RiverList.ItemsSource = labels;
        if (LblRiverCount is not null)
            LblRiverCount.Text = $"{ctx.Rivers.Count} river" + (ctx.Rivers.Count == 1 ? "" : "s");
    }

    private void OnRiverListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null || RiverList is null) { _activeRiverId = null; return; }
        int idx = RiverList.SelectedIndex;
        _activeRiverId = (idx >= 0 && idx < ctx.Rivers.Count) ? ctx.Rivers[idx].Id : null;
        RefreshRiverList();  // repaint the ● marker
    }

    private void OnNewRiverClick(object? sender, RoutedEventArgs e)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;
        int nextN = ctx.Rivers.Count + 1;
        var river = new L22.Editor.Models.Rivers.River
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"River {nextN}",
        };
        var cmd = new L22.Editor.Models.Commands.AddRiverCommand(ctx, river);
        ctx.History.ExecuteAndPush(cmd);
        _activeRiverId = river.Id;
        RefreshRiverList();
    }

    private void OnDeleteRiverClick(object? sender, RoutedEventArgs e)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null || RiverList is null || RiverList.SelectedIndex < 0) return;
        if (RiverList.SelectedIndex >= ctx.Rivers.Count) return;
        var r = ctx.Rivers[RiverList.SelectedIndex];
        var cmd = new L22.Editor.Models.Commands.DeleteRiverCommand(ctx, r);
        ctx.History.ExecuteAndPush(cmd);
        if (_activeRiverId == r.Id) _activeRiverId = null;
    }

    private void OnRiverVertexAddRequested(float worldX, float worldZ)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;

        int cx = (int)Math.Floor(worldX);
        int cy = (int)Math.Floor(worldZ);
        float sx = worldX - cx;
        float sy = worldZ - cy;
        var vertex = new L22.Editor.Models.Rivers.RiverVertex
        {
            CellX = cx, CellY = cy,
            SubCellX = sx, SubCellY = sy,
            WidthWorld = 1.0f,
        };

        // Scenario 2: if a vertex is selected, the next empty-space click
        // spawns a branch starting at that vertex's position and extending
        // to the new point. The branch's anchor copies the parent's bezier
        // out-handle so the fork flows naturally away from the parent at
        // the selection — per journey update "defaults to the flow
        // direction of the node I selected".
        if (ctx.SelectedRiverVertex is { } sel)
        {
            var parent = FindRiverRecursive(ctx, sel.riverId);
            if (parent is not null && sel.index >= 0 && sel.index < parent.Vertices.Count)
            {
                var anchor = parent.Vertices[sel.index];
                var flow = GetFlowDirectionAt(parent, sel.index);
                var branch = new L22.Editor.Models.Rivers.River
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = $"{parent.Name} branch",
                    ForkFromIndex = sel.index,
                    Vertices =
                    {
                        new L22.Editor.Models.Rivers.RiverVertex
                        {
                            CellX = anchor.CellX, CellY = anchor.CellY,
                            SubCellX = anchor.SubCellX, SubCellY = anchor.SubCellY,
                            WidthWorld = anchor.WidthWorld,
                            BezierHandleOut = flow,
                        },
                        vertex,
                    },
                };
                var bCmd = new L22.Editor.Models.Commands.AddBranchCommand(ctx, parent.Id, branch);
                ctx.History.ExecuteAndPush(bCmd);
                // Move the selection to the tail vertex of the new branch
                // so the next click *extends* this branch (natural flow),
                // rather than silently creating a sibling branch off the
                // original anchor.
                ctx.SelectedRiverVertices.Clear();
                ctx.SelectedRiverVertices.Add((branch.Id, 1));
                ctx.SelectedRiverVertex = (branch.Id, 1);
                ctx.RaiseSelectedRiverVertexChanged();
                RefreshRiverVertexPanel();
                RefreshRiverOverlay();
                return;
            }
        }

        // No selection → append to the active river (creating one if the
        // user hasn't started any yet).
        L22.Editor.Models.Rivers.River? active = null;
        if (_activeRiverId is not null)
            foreach (var r in ctx.Rivers)
                if (r.Id == _activeRiverId) { active = r; break; }
        if (active is null)
        {
            active = new L22.Editor.Models.Rivers.River
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = $"River {ctx.Rivers.Count + 1}",
            };
            var addCmd = new L22.Editor.Models.Commands.AddRiverCommand(ctx, active);
            ctx.History.ExecuteAndPush(addCmd);
            _activeRiverId = active.Id;
        }
        var vCmd = new L22.Editor.Models.Commands.AddRiverVertexCommand(ctx, active.Id, vertex);
        ctx.History.ExecuteAndPush(vCmd);
    }

    private void OnRiverVertexSelectRequested(string riverId, int index, bool addToSelection)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;
        if (!addToSelection) ctx.SelectedRiverVertices.Clear();
        ctx.SelectedRiverVertices.Add((riverId, index));
        ctx.SelectedRiverVertex = (riverId, index);
        ctx.RaiseSelectedRiverVertexChanged();
        RefreshRiverVertexPanel();
        RefreshRiverOverlay();
    }

    private void RefreshRiverVertexPanel()
    {
        if (PanRiverVertex is null) return;
        var ctx = ViewModel.Active?.Context;
        if (ctx?.SelectedRiverVertex is not { } sel)
        {
            PanRiverVertex.IsVisible = false;
            return;
        }
        var r = FindRiverRecursive(ctx, sel.riverId);
        if (r is null || sel.index < 0 || sel.index >= r.Vertices.Count)
        {
            PanRiverVertex.IsVisible = false;
            return;
        }
        PanRiverVertex.IsVisible = true;
        int multi = ctx.SelectedRiverVertices.Count;
        LblRiverVertexInfo.Text = multi > 1
            ? $"{multi} vertices selected"
            : $"{r.Name} · vertex {sel.index}";
        float w = r.Vertices[sel.index].WidthWorld;
        LblRiverWidth.Text = w.ToString("0.00");
        _widthSliderQuiet = true;
        try { SlRiverWidth.Value = Math.Clamp(w, SlRiverWidth.Minimum, SlRiverWidth.Maximum); }
        finally { _widthSliderQuiet = false; }
    }

    private bool _widthSliderQuiet;
    private void OnRiverWidthSliderChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_widthSliderQuiet) return;
        if (LblRiverWidth is not null) LblRiverWidth.Text = e.NewValue.ToString("0.00");
    }

    // Push the command only when the user lets go of the slider — avoids
    // flooding the history stack with one command per sub-pixel drag.
    // Fires on PointerCaptureLost (end of mouse drag / click-release on
    // the track) and on LostFocus (keyboard-arrows then tab away). Walks
    // every selected vertex so the multi-select case ("I have one or more
    // vertices") lifts them all at once.
    private void OnRiverWidthCommit(object? sender, RoutedEventArgs e)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;
        if (ctx.SelectedRiverVertices.Count == 0) return;
        float after = (float)SlRiverWidth.Value;
        bool anyChanged = false;
        foreach (var key in ctx.SelectedRiverVertices)
        {
            var r = FindRiverRecursive(ctx, key.riverId);
            if (r is null || key.index < 0 || key.index >= r.Vertices.Count) continue;
            float before = r.Vertices[key.index].WidthWorld;
            if (System.Math.Abs(before - after) < 1e-4f) continue;
            var cmd = new L22.Editor.Models.Commands.SetRiverVertexWidthCommand(
                ctx, key.riverId, key.index, after);
            ctx.History.ExecuteAndPush(cmd);
            anyChanged = true;
        }
        if (!anyChanged) return;  // all vertices already at target
    }

    private void OnRiverHandleDragPreview(string riverId, int index, float x, float y)
    {
        // Model is already mutated by the viewport mid-drag; just repaint
        // the overlay so the preview tracks the cursor without waiting
        // for the 60ms DispatcherTimer.
        RefreshRiverOverlay();
    }

    private void OnRiverHandleDragRelease(string riverId, int index, float x, float y)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;
        var r = FindRiverRecursive(ctx, riverId);
        if (r is null || index < 0 || index >= r.Vertices.Count) return;
        var before = r.Vertices[index].BezierHandleOut;
        var after = new System.Numerics.Vector2(x, y);
        if (System.Math.Abs(before.X - after.X) < 1e-4f && System.Math.Abs(before.Y - after.Y) < 1e-4f)
            return;
        var cmd = new L22.Editor.Models.Commands.SetRiverVertexHandleCommand(
            ctx, riverId, index, before, after);
        ctx.History.ExecuteAndPush(cmd);
    }

    // Local flow direction at a river vertex: tangent at index i. If i is
    // the last vertex, falls back to (vi - vi-1); if first, (vi+1 - vi).
    // Scale is tiny — this feeds a BezierHandleOut, which is a short
    // tangent offset not a full segment.
    private static System.Numerics.Vector2 GetFlowDirectionAt(
        L22.Editor.Models.Rivers.River river, int index)
    {
        if (river.Vertices.Count < 2) return System.Numerics.Vector2.Zero;
        var cur = river.Vertices[index];
        L22.Editor.Models.Rivers.RiverVertex a, b;
        if (index + 1 < river.Vertices.Count) { a = cur; b = river.Vertices[index + 1]; }
        else                                   { a = river.Vertices[index - 1]; b = cur; }
        float dx = b.WorldX - a.WorldX;
        float dz = b.WorldZ - a.WorldZ;
        float len = System.MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1e-5f) return System.Numerics.Vector2.Zero;
        float scale = 0.5f;
        return new System.Numerics.Vector2(dx / len * scale, dz / len * scale);
    }

    private static L22.Editor.Models.Rivers.River? FindRiverRecursive(EditorContext ctx, string id)
    {
        foreach (var r in ctx.Rivers)
        {
            var hit = FindRecursive(r, id);
            if (hit is not null) return hit;
        }
        return null;
    }
    private static L22.Editor.Models.Rivers.River? FindRecursive(L22.Editor.Models.Rivers.River r, string id)
    {
        if (r.Id == id) return r;
        foreach (var b in r.Branches)
        {
            var hit = FindRecursive(b, id);
            if (hit is not null) return hit;
        }
        return null;
    }

    // Build a StreamGeometry with the polylines of every river and small
    // circles at each vertex, in viewport-local pixel coords.
    private void RefreshRiverOverlay()
    {
        if (RiverOverlay is null) return;
        var ctx = ViewModel.Active?.Context;
        if (ctx is null || ctx.Rivers.Count == 0)
        {
            RiverOverlay.Data = null;
            if (RiverSelectedOverlay is not null) RiverSelectedOverlay.Data = null;
            return;
        }

        int w = Math.Max(1, (int)EditViewport.Bounds.Width);
        int h = Math.Max(1, (int)EditViewport.Bounds.Height);
        if (w <= 1 || h <= 1) { RiverOverlay.Data = null; return; }
        float aspect = (float)w / h;
        var cam = EditViewport.Camera;
        if (!cam.IsOrthographic) { RiverOverlay.Data = null; return; }
        float halfH = cam.OrthoHalfHeight;
        float halfW = halfH * aspect;

        Point ToScreen(float wx, float wz)
        {
            float sx = (wx - cam.Position.X) / halfW;
            float sy = -(wz - cam.Position.Z) / halfH;
            return new Point((sx + 1) * 0.5 * w, (1 - sy) * 0.5 * h);
        }

        // Pixels per world unit for scaling the "radius = width" rings.
        double worldToScreen = (h * 0.5) / halfH;
        var g = new StreamGeometry();
        using (var ctxDraw = g.Open())
        {
            foreach (var r in ctx.Rivers) WalkRiver(ctxDraw, r, ToScreen, worldToScreen);
        }
        RiverOverlay.Data = g;

        if (RiverSelectedOverlay is not null)
        {
            if (ctx.SelectedRiverVertices.Count == 0)
            {
                RiverSelectedOverlay.Data = null;
            }
            else
            {
                var hg = new StreamGeometry();
                using (var dc = hg.Open())
                {
                    foreach (var key in ctx.SelectedRiverVertices)
                    {
                        var selRiver = FindRiverRecursive(ctx, key.riverId);
                        if (selRiver is null || key.index < 0 || key.index >= selRiver.Vertices.Count) continue;
                        var v = selRiver.Vertices[key.index];
                        var p = ToScreen(v.WorldX, v.WorldZ);
                        dc.BeginFigure(new Point(p.X - 6, p.Y - 6), isFilled: true);
                        dc.LineTo(new Point(p.X + 6, p.Y - 6));
                        dc.LineTo(new Point(p.X + 6, p.Y + 6));
                        dc.LineTo(new Point(p.X - 6, p.Y + 6));
                        dc.EndFigure(isClosed: true);
                    }
                }
                RiverSelectedOverlay.Data = hg;
            }
        }
    }

    private void WalkRiver(StreamGeometryContext ctx, L22.Editor.Models.Rivers.River r,
                            Func<float, float, Point> toScreen, double worldToScreen)
    {
        if (r.Vertices.Count >= 2)
        {
            var p0 = toScreen(r.Vertices[0].WorldX, r.Vertices[0].WorldZ);
            ctx.BeginFigure(p0, isFilled: false);
            for (int i = 1; i < r.Vertices.Count; i++)
                ctx.LineTo(toScreen(r.Vertices[i].WorldX, r.Vertices[i].WorldZ));
            ctx.EndFigure(isClosed: false);
        }
        // Draw each vertex handle as a filled square at the centre, plus a
        // ring whose radius equals the vertex's width/2 in world units
        // (journey: "its radius from the centre is its weighting"). A
        // small filled square at vertex + BezierHandleOut is the draggable
        // bezier direction handle.
        foreach (var v in r.Vertices)
        {
            var p = toScreen(v.WorldX, v.WorldZ);
            ctx.BeginFigure(new Point(p.X - 3, p.Y - 3), isFilled: true);
            ctx.LineTo(new Point(p.X + 3, p.Y - 3));
            ctx.LineTo(new Point(p.X + 3, p.Y + 3));
            ctx.LineTo(new Point(p.X - 3, p.Y + 3));
            ctx.EndFigure(isClosed: true);

            // 5 px floor so the ring stays distinct from the 3-px vertex
            // square at zoom-out levels — otherwise the two visually merge.
            double rPx = Math.Max(5, v.WidthWorld * 0.5 * worldToScreen);
            const int segs = 16;
            ctx.BeginFigure(new Point(p.X + rPx, p.Y), isFilled: false);
            for (int i = 1; i <= segs; i++)
            {
                double ang = (i / (double)segs) * Math.PI * 2;
                ctx.LineTo(new Point(p.X + rPx * Math.Cos(ang), p.Y + rPx * Math.Sin(ang)));
            }
            ctx.EndFigure(isClosed: false);

            // Bezier out-handle: a small filled square at the handle tip,
            // plus a line from vertex centre so the direction is visually
            // obvious.
            float hx = v.WorldX + v.BezierHandleOut.X;
            float hz = v.WorldZ + v.BezierHandleOut.Y;
            var hp = toScreen(hx, hz);
            ctx.BeginFigure(p, isFilled: false);
            ctx.LineTo(hp);
            ctx.EndFigure(isClosed: false);
            ctx.BeginFigure(new Point(hp.X - 3, hp.Y - 3), isFilled: true);
            ctx.LineTo(new Point(hp.X + 3, hp.Y - 3));
            ctx.LineTo(new Point(hp.X + 3, hp.Y + 3));
            ctx.LineTo(new Point(hp.X - 3, hp.Y + 3));
            ctx.EndFigure(isClosed: true);
        }
        foreach (var b in r.Branches) WalkRiver(ctx, b, toScreen, worldToScreen);
    }

    private void OnSelectToolRiver(object? s, RoutedEventArgs e)
    {
        if (ViewModel.Brush is { } b) b.ActiveTool = ToolKind.River;
        EditViewport.CornerToolActive = false;
        EditViewport.DropperToolActive = false;
        EditViewport.PlaceObjectToolActive = false;
        EditViewport.RiverToolActive = true;
        EditViewport.ClearSelectedCorners();
        EditViewportHost.Cursor = new Cursor(StandardCursorType.Cross);
        BrushOverlay.IsVisible = false;
        DropperOverlay.IsVisible = false;
        BoxSelectOverlay.IsVisible = false;
        PlaceObjectOverlay.IsVisible = false;
        UpdateLayerUi();
    }

    // Any object mutation (place / delete / modal-edit / undo / redo) comes
    // through here. Rebuild the list, then re-point the property tree at the
    // currently-selected object's PropertyOverrides — EditInstanceCommand
    // swaps the whole list reference on undo, so the TreeView would otherwise
    // keep showing the pre-undo items.
    private void OnBoundObjectsChanged()
    {
        RefreshObjectList();
        if (PropertyTree is null) return;
        var inst = SelectedObjectInstance();
        PropertyTree.ItemsSource = inst?.PropertyOverrides;
    }

    private string? _selectedPrefabId;

    private void RefreshPrefabList()
    {
        if (PrefabList is null) return;
        var ctx = ViewModel.Active?.Context;
        if (ctx is null)
        {
            PrefabList.ItemsSource = null;
            if (LblPrefabCount is not null) LblPrefabCount.Text = "0 prefabs";
            return;
        }
        var all = new List<L22.Editor.Models.Objects.ObjectPrefab>(ctx.Prefabs.All);
        var labels = new List<string>(all.Count);
        foreach (var p in all)
        {
            string name = string.IsNullOrEmpty(p.DisplayName) ? "(unnamed)" : p.DisplayName;
            labels.Add($"{name}  ·  {p.Id}");
        }
        PrefabList.ItemsSource = labels;
        if (LblPrefabCount is not null)
            LblPrefabCount.Text = $"{all.Count} prefab" + (all.Count == 1 ? "" : "s");
        if (LblPrefabLibDir is not null)
            LblPrefabLibDir.Text = $"Library: {ctx.Prefabs.LibraryDir ?? "(unset)"}";
    }

    private void OnPrefabListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null || PrefabList is null)
        {
            _selectedPrefabId = null;
            return;
        }
        var all = new List<L22.Editor.Models.Objects.ObjectPrefab>(ctx.Prefabs.All);
        int idx = PrefabList.SelectedIndex;
        _selectedPrefabId = (idx >= 0 && idx < all.Count) ? all[idx].Id : null;
    }

    private void OnNewPrefabInLibrary(object? sender, RoutedEventArgs e)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;
        if (ctx.Prefabs.LibraryDir is null)
        {
            LblPrefabLibDir.Text = "Library: (save the scene first)";
            return;
        }
        var p = new L22.Editor.Models.Objects.ObjectPrefab
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = "Untitled prefab",
            ForeignIdentifier = "",
        };
        ctx.Prefabs.Upsert(p);
    }

    private void OnDeletePrefabFromLibrary(object? sender, RoutedEventArgs e)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null || _selectedPrefabId is null) return;
        ctx.Prefabs.Remove(_selectedPrefabId);
        _selectedPrefabId = null;
    }

    private string PrefabForNewObject() => _selectedPrefabId ?? "none";

    // Row-index in the (sorted + filtered) ListBox → real ObjectInstance.
    // Rebuilt every RefreshObjectList call; selection handlers use it.
    private readonly List<L22.Editor.Models.Objects.ObjectInstance> _displayedObjects = new();

    private void RefreshObjectList()
    {
        if (ObjectList is null) return;
        var ctx = ViewModel.Active?.Context;
        _displayedObjects.Clear();
        if (ctx is null)
        {
            ObjectList.ItemsSource = null;
            if (LblObjectCount is not null) LblObjectCount.Text = "0 objects";
            return;
        }

        string filter = (TxtObjectSearch?.Text ?? "").Trim().ToLowerInvariant();

        var ranked = new List<(string display, string code, L22.Editor.Models.Objects.ObjectInstance inst)>(ctx.Objects.Count);
        foreach (var o in ctx.Objects)
        {
            var prefab = ctx.Prefabs.Find(o.PrefabId);
            string display = !string.IsNullOrEmpty(o.NameOverride) ? o.NameOverride!
                : !string.IsNullOrEmpty(prefab?.DisplayName) ? prefab!.DisplayName
                : "(unnamed)";
            string code = !string.IsNullOrEmpty(o.CodeName) ? o.CodeName!
                : !string.IsNullOrEmpty(prefab?.ForeignIdentifier) ? prefab!.ForeignIdentifier
                : "";
            if (filter.Length > 0)
            {
                bool hit = display.ToLowerInvariant().Contains(filter)
                        || code.ToLowerInvariant().Contains(filter)
                        || o.Id.ToLowerInvariant().Contains(filter);
                if (!hit) continue;
            }
            ranked.Add((display, code, o));
        }
        ranked.Sort((a, b) =>
        {
            int d = string.Compare(a.display, b.display, System.StringComparison.OrdinalIgnoreCase);
            if (d != 0) return d;
            return string.Compare(a.code, b.code, System.StringComparison.OrdinalIgnoreCase);
        });

        var labels = new List<string>(ranked.Count);
        foreach (var r in ranked)
        {
            _displayedObjects.Add(r.inst);
            string code = string.IsNullOrEmpty(r.code) ? "" : $"  ·  {r.code}";
            labels.Add($"{r.display}{code}  ·  ({r.inst.CellX}, {r.inst.CellY})");
        }
        ObjectList.ItemsSource = labels;
        if (LblObjectCount is not null)
            LblObjectCount.Text = ranked.Count == ctx.Objects.Count
                ? $"{ctx.Objects.Count} object" + (ctx.Objects.Count == 1 ? "" : "s")
                : $"{ranked.Count} / {ctx.Objects.Count}";
    }

    private void OnObjectSearchChanged(object? sender, TextChangedEventArgs e) => RefreshObjectList();

    private L22.Editor.Models.Objects.ObjectInstance? SelectedObjectInstance()
    {
        int idx = ObjectList?.SelectedIndex ?? -1;
        if (idx < 0 || idx >= _displayedObjects.Count) return null;
        return _displayedObjects[idx];
    }

    private async void OnObjectListDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e) => await OpenObjectEditor();
    private async void OnEditObjectClick(object? sender, RoutedEventArgs e) => await OpenObjectEditor();

    private async System.Threading.Tasks.Task OpenObjectEditor()
    {
        var ctx = ViewModel.Active?.Context;
        var inst = SelectedObjectInstance();
        if (ctx is null || inst is null) return;

        var payload = new L22.Editor.Views.ObjectEditorPayload
        {
            Title = $"Edit object  ·  {inst.Id}",
            SubjectLabel = $"Object  ·  cell ({inst.CellX}, {inst.CellY})",
            CodeNameLabel = "Code name:",
            DisplayName = inst.NameOverride ?? "",
            CodeName = inst.CodeName ?? "",
            Properties = L22.Editor.Models.Objects.PropertyNode.DeepClone(inst.PropertyOverrides),
            ShowPrefabPicker = true,
            AvailablePrefabs = new System.Collections.Generic.List<L22.Editor.Models.Objects.ObjectPrefab>(ctx.Prefabs.All),
            SelectedPrefabId = inst.PrefabId == "none" ? null : inst.PrefabId,
        };

        var dialog = new L22.Editor.Views.ObjectEditorDialog(payload);
        payload.EditPrefabRequested += async (prefabId) =>
        {
            var pf = ctx.Prefabs.Find(prefabId);
            if (pf is null) return;
            await OpenPrefabEditorNested(ctx, pf, dialog);
            // Refresh available list — DisplayName may have changed.
            // Simpler: just leave the picker as-is; user will see updates
            // after dialog closes.
        };
        bool ok = await dialog.ShowDialog<bool>(this);
        if (!ok) return;

        var cmd = new L22.Editor.Models.Commands.EditInstanceCommand(
            ctx, inst,
            newName: string.IsNullOrEmpty(payload.DisplayName) ? null : payload.DisplayName,
            newCode: string.IsNullOrEmpty(payload.CodeName) ? null : payload.CodeName,
            newPrefabId: payload.SelectedPrefabId,
            newProps: payload.Properties);
        ctx.History.ExecuteAndPush(cmd);
    }

    private async System.Threading.Tasks.Task OpenPrefabEditorNested(EditorContext ctx,
        L22.Editor.Models.Objects.ObjectPrefab pf, Window owner)
    {
        var payload = new L22.Editor.Views.ObjectEditorPayload
        {
            Title = $"Edit prefab  ·  {pf.Id}",
            SubjectLabel = $"Prefab  ·  id {pf.Id}",
            CodeNameLabel = "Foreign id:",
            DisplayName = pf.DisplayName ?? "",
            CodeName = pf.ForeignIdentifier ?? "",
            Properties = L22.Editor.Models.Objects.PropertyNode.DeepClone(pf.Properties),
        };
        var dlg = new L22.Editor.Views.ObjectEditorDialog(payload);
        bool ok = await dlg.ShowDialog<bool>(owner);
        if (!ok) return;
        pf.DisplayName = payload.DisplayName;
        pf.ForeignIdentifier = payload.CodeName;
        pf.Properties = payload.Properties;
        ctx.Prefabs.Upsert(pf);
    }

    private async void OnEditPrefabsClick(object? sender, RoutedEventArgs e)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;

        L22.Editor.Models.Objects.ObjectPrefab? target = null;
        if (_selectedPrefabId is { } sid) target = ctx.Prefabs.Find(sid);

        bool created = false;
        if (target is null)
        {
            if (ctx.Prefabs.LibraryDir is null)
            {
                LblPrefabLibDir.Text = "Library: (save the scene first)";
                return;
            }
            target = new L22.Editor.Models.Objects.ObjectPrefab
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = "Untitled prefab",
            };
            created = true;
        }

        var payload = new L22.Editor.Views.ObjectEditorPayload
        {
            Title = $"Edit prefab  ·  {target.Id}",
            SubjectLabel = $"Prefab  ·  id {target.Id}",
            CodeNameLabel = "Foreign id:",
            DisplayName = target.DisplayName ?? "",
            CodeName = target.ForeignIdentifier ?? "",
            Properties = L22.Editor.Models.Objects.PropertyNode.DeepClone(target.Properties),
        };
        bool ok = await L22.Editor.Views.ObjectEditorDialog.ShowAsync(this, payload);
        if (!ok)
        {
            // Nothing to roll back for an existing prefab; a just-created
            // one never hit the library so there's also nothing to clean.
            return;
        }
        target.DisplayName = payload.DisplayName;
        target.ForeignIdentifier = payload.CodeName;
        target.Properties = payload.Properties;
        ctx.Prefabs.Upsert(target);
        if (created) _selectedPrefabId = target.Id;
    }

    private void OnObjectListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PropertyTree is null) return;
        var inst = SelectedObjectInstance();
        if (inst is null)
        {
            PropertyTree.ItemsSource = null;
            PanPropertyEditor.IsVisible = false;
            return;
        }
        PropertyTree.ItemsSource = inst.PropertyOverrides;
        PanPropertyEditor.IsVisible = false;
        EditViewport.RequestFrame();
    }

    private L22.Editor.Models.Objects.PropertyNode? _editingNode;

    private void OnPropertyTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PropertyTree?.SelectedItem is not L22.Editor.Models.Objects.PropertyNode node)
        {
            PanPropertyEditor.IsVisible = false;
            _editingNode = null;
            return;
        }
        // Only leaves are editable here; group nodes get add/remove in a
        // later pass.
        if (node.Kind is not L22.Editor.Models.Objects.PropertyKind.Text
                     and not L22.Editor.Models.Objects.PropertyKind.Number)
        {
            PanPropertyEditor.IsVisible = false;
            _editingNode = null;
            return;
        }
        _editingNode = node;
        LblPropertyPath.Text = $"{node.Kind.ToString().ToLowerInvariant()}  ·  {node.Name}";
        TxtPropertyValue.Text = node.Kind == L22.Editor.Models.Objects.PropertyKind.Text
            ? node.TextValue ?? ""
            : (node.NumberValue?.ToString() ?? "");
        PanPropertyEditor.IsVisible = true;
    }

    private void OnApplyPropertyEdit(object? sender, RoutedEventArgs e)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null || _editingNode is null) return;
        string input = TxtPropertyValue?.Text ?? "";
        string? newText = null;
        double? newNumber = null;
        if (_editingNode.Kind == L22.Editor.Models.Objects.PropertyKind.Number)
        {
            if (!double.TryParse(input, out var parsed))
            {
                LblPropertyPath.Text += "  (not a number)";
                return;
            }
            newNumber = parsed;
        }
        else
        {
            newText = input;
        }
        var cmd = new L22.Editor.Models.Commands.EditPropertyCommand(ctx, _editingNode, newText, newNumber);
        ctx.History.ExecuteAndPush(cmd);
        // The command raises ObjectsChanged, which OnBoundObjectsChanged
        // picks up and re-points PropertyTree.ItemsSource for us.
    }

    private void OnRevertPropertyEdit(object? sender, RoutedEventArgs e)
    {
        if (_editingNode is null) return;
        TxtPropertyValue.Text = _editingNode.Kind == L22.Editor.Models.Objects.PropertyKind.Text
            ? _editingNode.TextValue ?? ""
            : (_editingNode.NumberValue?.ToString() ?? "");
    }

    private void OnAddObjectClick(object? sender, RoutedEventArgs e)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;
        var inst = new L22.Editor.Models.Objects.ObjectInstance
        {
            Id = Guid.NewGuid().ToString("N"),
            PrefabId = PrefabForNewObject(),
            CellX = 64,
            CellY = 64,
        };
        var cmd = new L22.Editor.Models.Commands.AddObjectCommand(ctx, inst);
        ctx.History.ExecuteAndPush(cmd);
    }

    private void OnDeleteObjectClick(object? sender, RoutedEventArgs e)
    {
        var ctx = ViewModel.Active?.Context;
        var inst = SelectedObjectInstance();
        if (ctx is null || inst is null) return;
        var cmd = new L22.Editor.Models.Commands.DeleteObjectCommand(ctx, inst);
        ctx.History.ExecuteAndPush(cmd);
    }

    private void RefreshCamToggle(Border toggle, TextBlock icon, bool active)
    {
        toggle.Background    = new SolidColorBrush(active ? Color.Parse("#C0FFA030") : Color.Parse("#80000000"));
        toggle.BorderBrush   = new SolidColorBrush(active ? Color.Parse("#FFFFC266") : Color.Parse("#80FFFFFF"));
        icon.Text = active ? "✕" : "◎";
    }

    // ---- camera-drive drag ---------------------------------------------------------

    private void OnEditCamPressed(object? s, PointerPressedEventArgs e)
    {
        // Entering free-cam on one viewport sends subsequent key routing to
        // it; drop any still-held keys on BOTH so no stale W drifts a camera
        // when free-cam ends.
        EditViewport.ClearHeldKeys();
        FreeViewport.ClearHeldKeys();
        _currentHoveredViewport = null;
        FreeViewport.SetCameraFree(false);
        EditViewport.SetCameraFree(true);
        e.Pointer.Capture(EditCamToggle);
        e.Handled = true;
    }

    private void OnEditCamReleased(object? s, RoutedEventArgs e)
    {
        EditViewport.SetCameraFree(false);
        if (e is PointerEventArgs pe) pe.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnFreeCamPressed(object? s, PointerPressedEventArgs e)
    {
        EditViewport.ClearHeldKeys();
        FreeViewport.ClearHeldKeys();
        _currentHoveredViewport = null;
        EditViewport.SetCameraFree(false);
        FreeViewport.SetCameraFree(true);
        e.Pointer.Capture(FreeCamToggle);
        e.Handled = true;
    }

    private void OnFreeCamReleased(object? s, RoutedEventArgs e)
    {
        FreeViewport.SetCameraFree(false);
        if (e is PointerEventArgs pe) pe.Pointer.Capture(null);
        e.Handled = true;
    }

    private bool IsOverCamToggle(PointerEventArgs e)
    {
        var src = e.Source as Visual;
        while (src is not null)
        {
            if (ReferenceEquals(src, EditCamToggle) || ReferenceEquals(src, FreeCamToggle)) return true;
            src = src.GetVisualParent();
        }
        return false;
    }

    // ---- window-level input dispatch ----------------------------------------------

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // Let any focused TextBox keep the keystroke; the camera-move keys
        // and shortcuts are irrelevant when the user is typing.
        if (IsSourceInsideTextBox(e)) return;

        // Hovering a slider? Numeric keypress starts an inline edit.
        if (_hoveredSlider is not null && NumericCharFromKey(e.Key) is { } first)
        {
            BeginInlineEdit(_hoveredSlider, first);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (EditViewport.CameraFree) { EditViewport.SetCameraFree(false); e.Handled = true; return; }
            if (FreeViewport.CameraFree) { FreeViewport.SetCameraFree(false); e.Handled = true; return; }
            // Esc also clears an in-scene object selection.
            var ctx = ViewModel.Active?.Context;
            if (ctx is { } c && c.SelectedObjectIds.Count > 0)
            {
                c.SelectedObjectIds.Clear();
                c.RaiseObjectSelectionChanged();
                SyncObjectListSelection();
                EditViewport.RequestFrame();
                FreeViewport.RequestFrame();
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Delete)
        {
            var ctx = ViewModel.Active?.Context;
            if (ctx is { } c && c.SelectedObjectIds.Count > 0)
            {
                // Snapshot — the Delete command we push mutates the list.
                var ids = new List<string>(c.SelectedObjectIds);
                foreach (var id in ids)
                {
                    var inst = c.Objects.Find(o => o.Id == id);
                    if (inst is not null)
                    {
                        var cmd = new L22.Editor.Models.Commands.DeleteObjectCommand(c, inst);
                        c.History.ExecuteAndPush(cmd);
                    }
                }
                c.SelectedObjectIds.Clear();
                c.RaiseObjectSelectionChanged();
                e.Handled = true;
                return;
            }
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Key == Key.Z) { ViewModel.Undo(); EditViewport.RequestFrame(); FreeViewport.RequestFrame(); e.Handled = true; return; }
            if (e.Key == Key.Y) { ViewModel.Redo(); EditViewport.RequestFrame(); FreeViewport.RequestFrame(); e.Handled = true; return; }
            if (e.Key == Key.S)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) OnSaveAs(this, e);
                else OnSave(this, e);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.O) { OnOpenFile(this, e); e.Handled = true; return; }
        }
        var target = EditViewport.CameraFree ? EditViewport
                   : FreeViewport.CameraFree ? FreeViewport
                   : _lastHoveredViewport;
        target?.ReceiveKeyDown(e);
        ApplySculptModeFromModifiers(e.KeyModifiers);
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        var target = EditViewport.CameraFree ? EditViewport
                   : FreeViewport.CameraFree ? FreeViewport
                   : _lastHoveredViewport;
        target?.ReceiveKeyUp(e);
        ApplySculptModeFromModifiers(e.KeyModifiers);
    }

    private TerrainViewport? HitViewport(PointerEventArgs e)
    {
        foreach (var vp in new[] { EditViewport, FreeViewport })
        {
            var p = e.GetPosition(vp);
            if (p.X >= 0 && p.Y >= 0 && p.X < vp.Bounds.Width && p.Y < vp.Bounds.Height)
                return vp;
        }
        return null;
    }

    private TerrainViewport? CameraFreeViewport
        => EditViewport.CameraFree ? EditViewport
         : FreeViewport.CameraFree ? FreeViewport
         : null;

    private void TrackHover(TerrainViewport? vp)
    {
        if (!ReferenceEquals(vp, _currentHoveredViewport))
        {
            // Pointer transitioned off the previous viewport. Drop its keys
            // so a WASD-held mouse-exit doesn't leave the camera drifting.
            _currentHoveredViewport?.ClearHeldKeys();
            _currentHoveredViewport = vp;
        }
        if (vp is not null) _lastHoveredViewport = vp;
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (CameraFreeViewport is { } cf) { cf.HandleWindowPointerMoved(e); return; }
        var vp = HitViewport(e);
        TrackHover(vp);
        vp?.HandleWindowPointerMoved(e);
        UpdateToolOverlays(e);
    }

    private void UpdateToolOverlays(PointerEventArgs e)
    {
        var brush = ViewModel.Brush;
        if (brush is null)
        {
            DropperOverlay.IsVisible = false;
            BoxSelectOverlay.IsVisible = false;
            BrushOverlay.IsVisible = false;
            PlaceObjectOverlay.IsVisible = false;
            return;
        }

        ApplySculptModeFromModifiers(e.KeyModifiers);

        var hostPos = e.GetPosition(EditViewportHost);
        bool overEdit = hostPos.X >= 0 && hostPos.Y >= 0
                      && hostPos.X < EditViewportHost.Bounds.Width
                      && hostPos.Y < EditViewportHost.Bounds.Height;

        bool wantDropper = brush.ActiveTool == ToolKind.Dropper         && overEdit;
        bool wantBox     = brush.ActiveTool == ToolKind.CornerElevation && overEdit;
        bool wantBrush   = brush.ActiveTool == ToolKind.Brush           && overEdit;
        bool wantPlace   = brush.ActiveTool == ToolKind.PlaceObject     && overEdit;

        DropperOverlay.IsVisible     = wantDropper;
        BoxSelectOverlay.IsVisible   = wantBox;
        BrushOverlay.IsVisible       = wantBrush;
        PlaceObjectOverlay.IsVisible = wantPlace;

        if (wantDropper)
        {
            // Hotspot = bottom-left tip of the pipette.
            Canvas.SetLeft(DropperOverlay, hostPos.X - 3);
            Canvas.SetTop(DropperOverlay, hostPos.Y - DropperOverlay.Height + 3);
        }
        if (wantBox)
        {
            Canvas.SetLeft(BoxSelectOverlay, hostPos.X - 3);
            Canvas.SetTop(BoxSelectOverlay, hostPos.Y - 3);
        }
        if (wantBrush)
        {
            // Centre the sculpt-mode icon on the pointer — unlike the paint
            // brush, there is no "tip" to align with.
            Canvas.SetLeft(BrushOverlay, hostPos.X - BrushOverlay.Width / 2);
            Canvas.SetTop(BrushOverlay, hostPos.Y - BrushOverlay.Height / 2);
        }
        if (wantPlace)
        {
            Canvas.SetLeft(PlaceObjectOverlay, hostPos.X - PlaceObjectOverlay.Width / 2);
            Canvas.SetTop(PlaceObjectOverlay, hostPos.Y - PlaceObjectOverlay.Height / 2);
        }
    }

    private void ApplySculptModeFromModifiers(KeyModifiers mods)
    {
        var brush = ViewModel.Brush;
        if (brush is null) return;
        var mode = BrushViewModel.SculptModeFor(mods);
        brush.SculptMode = mode;

        string key = mode switch
        {
            SculptMode.Subtractive => "IconBrushSub",
            SculptMode.Smooth      => "IconBrushSmooth",
            SculptMode.Unsmooth    => "IconBrushUnsmooth",
            _                      => "IconBrushAdd",
        };
        if (BrushOverlayPath is not null
            && Application.Current!.Resources.TryGetResource(key, null, out var res)
            && res is Geometry geo)
        {
            BrushOverlayPath.Data = geo;
        }
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsOverCamToggle(e)) return;
        if (CameraFreeViewport is not null) return;
        var vp = HitViewport(e);
        TrackHover(vp);
        vp?.HandleWindowPointerPressed(e);
    }

    private void OnWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (CameraFreeViewport is not null) return;
        var vp = HitViewport(e);
        TrackHover(vp);
        (vp ?? _lastHoveredViewport ?? EditViewport).HandleWindowPointerReleased(e);
    }

    private void OnWindowPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (CameraFreeViewport is { } cf) { cf.HandleWindowPointerWheel(e); return; }
        var vp = HitViewport(e);
        TrackHover(vp);
        vp?.HandleWindowPointerWheel(e);
    }

    // ---- commands & UI --------------------------------------------------------------

    private void UpdateLayerUi()
    {
        var brush = ViewModel.Brush;
        if (brush is null) return;
        bool cornerTool = brush.ActiveTool == ToolKind.CornerElevation;
        bool dropperTool = brush.ActiveTool == ToolKind.Dropper;
        LblActiveLayer.Text = cornerTool  ? "Tool: Corner Elevation"
                           : dropperTool ? $"Tool: Dropper — {brush.ActiveLayer}"
                           : $"Layer: {brush.ActiveLayer}";
        // Dropper reuses the brush panels so the sampled values are visible.
        PanElevationOptions.IsVisible   = !cornerTool && brush.ActiveLayer == LayerKind.Elevation;
        PanTerrainTypeOptions.IsVisible = !cornerTool && brush.ActiveLayer == LayerKind.TerrainType;
        PanCornerOptions.IsVisible      = cornerTool;
        // Corner tool is always about heights — show the same greyscale
        // elevation indication as Elevation-layer painting.
        EditViewport.ShowElevationGreyscale = cornerTool || brush.ActiveLayer == LayerKind.Elevation;
        if (brush.ActiveTool == ToolKind.Brush)
            EditViewportHost.Cursor = new Cursor(StandardCursorType.None);
        UpdateCornerPanel();
        EditViewport.RequestFrame();
    }

    private void OnExit(object? s, RoutedEventArgs e) => Close();

    private void OnUndo(object? s, RoutedEventArgs e)
    {
        ViewModel.Undo();
        EditViewport.RequestFrame();
        FreeViewport.RequestFrame();
    }

    private void OnRedo(object? s, RoutedEventArgs e)
    {
        ViewModel.Redo();
        EditViewport.RequestFrame();
        FreeViewport.RequestFrame();
    }

    private void OnResetCameras(object? s, RoutedEventArgs e)
    {
        EditViewport.ConfigureTopDown();
        FreeViewport.Camera.Position = new System.Numerics.Vector3(140, 55, 160);
        FreeViewport.Camera.Yaw = -0.65f;
        FreeViewport.Camera.Pitch = -0.45f;
        EditViewport.RequestFrame();
        FreeViewport.RequestFrame();
    }

    private void OnSelectLayerElev(object? s, RoutedEventArgs e) => ViewModel.SetActiveLayer(LayerKind.Elevation);
    private void OnSelectLayerType(object? s, RoutedEventArgs e) => ViewModel.SetActiveLayer(LayerKind.TerrainType);

    private void OnSelectToolBrush(object? s, RoutedEventArgs e)
    {
        if (ViewModel.Brush is { } b) b.ActiveTool = ToolKind.Brush;
        EditViewport.CornerToolActive = false;
        EditViewport.DropperToolActive = false;
        EditViewport.PlaceObjectToolActive = false;
        EditViewport.RiverToolActive = false;
        EditViewport.ClearSelectedCorners();
        EditViewportHost.Cursor = new Cursor(StandardCursorType.None);
        DropperOverlay.IsVisible = false;
        BoxSelectOverlay.IsVisible = false;
        PlaceObjectOverlay.IsVisible = false;
        UpdateLayerUi();
    }

    private void OnSelectToolCorners(object? s, RoutedEventArgs e)
    {
        if (ViewModel.Brush is { } b) b.ActiveTool = ToolKind.CornerElevation;
        EditViewport.CornerToolActive = true;
        EditViewport.DropperToolActive = false;
        EditViewport.PlaceObjectToolActive = false;
        EditViewport.RiverToolActive = false;
        // Hide the OS cursor and let the Avalonia Path overlay represent it.
        EditViewportHost.Cursor = new Cursor(StandardCursorType.None);
        BrushOverlay.IsVisible = false;
        DropperOverlay.IsVisible = false;
        PlaceObjectOverlay.IsVisible = false;
        UpdateLayerUi();
        EditViewport.RequestFrame();
    }

    private void OnSelectToolDropper(object? s, RoutedEventArgs e)
    {
        if (ViewModel.Brush is { } b) b.ActiveTool = ToolKind.Dropper;
        EditViewport.CornerToolActive = false;
        EditViewport.DropperToolActive = true;
        EditViewport.PlaceObjectToolActive = false;
        EditViewport.RiverToolActive = false;
        EditViewport.ClearSelectedCorners();
        EditViewportHost.Cursor = new Cursor(StandardCursorType.None);
        BrushOverlay.IsVisible = false;
        BoxSelectOverlay.IsVisible = false;
        PlaceObjectOverlay.IsVisible = false;
        UpdateLayerUi();
    }

    private void OnSelectToolPlaceObject(object? s, RoutedEventArgs e)
    {
        if (ViewModel.Brush is { } b) b.ActiveTool = ToolKind.PlaceObject;
        EditViewport.CornerToolActive = false;
        EditViewport.DropperToolActive = false;
        EditViewport.PlaceObjectToolActive = true;
        EditViewport.RiverToolActive = false;
        EditViewport.ClearSelectedCorners();
        // Hide the OS cursor; the pink diamond overlay represents the drop
        // site. Matches the Brush / Corner / Dropper tools' pattern.
        EditViewportHost.Cursor = new Cursor(StandardCursorType.None);
        BrushOverlay.IsVisible = false;
        BoxSelectOverlay.IsVisible = false;
        DropperOverlay.IsVisible = false;
        UpdateLayerUi();
    }

    private void OnObjectBoxSelectRequested(float wx0, float wz0, float wx1, float wz1)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;
        ctx.SelectedObjectIds.Clear();
        foreach (var o in ctx.Objects)
        {
            float cx = o.CellX + 0.5f;
            float cz = o.CellY + 0.5f;
            if (cx >= wx0 && cx <= wx1 && cz >= wz0 && cz <= wz1)
                ctx.SelectedObjectIds.Add(o.Id);
        }
        ctx.RaiseObjectSelectionChanged();
        SyncObjectListSelection();
        EditViewport.RequestFrame();
        FreeViewport.RequestFrame();
    }

    // Mirror the context's selected ids into the ObjectList's selection
    // so a drag-select over the viewport highlights the matching rows.
    private void SyncObjectListSelection()
    {
        if (ObjectList is null) return;
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;
        ObjectList.SelectedItems?.Clear();
        for (int i = 0; i < _displayedObjects.Count; i++)
            if (ctx.SelectedObjectIds.Contains(_displayedObjects[i].Id))
                ObjectList.SelectedItems?.Add(ObjectList.Items[i]);
    }

    private void OnObjectSingleSelectRequested(string id)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;
        ctx.SelectedObjectIds.Clear();
        ctx.SelectedObjectIds.Add(id);
        ctx.RaiseObjectSelectionChanged();
        SyncObjectListSelection();
        // Scroll the list to the clicked row and show its properties.
        for (int i = 0; i < _displayedObjects.Count; i++)
        {
            if (_displayedObjects[i].Id == id)
            {
                ObjectList.SelectedIndex = i;
                ObjectList.ScrollIntoView(ObjectList.Items[i]);
                break;
            }
        }
        EditViewport.RequestFrame();
        FreeViewport.RequestFrame();
    }

    private async void OnObjectEditFromViewport(string id)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;
        var inst = ctx.Objects.Find(o => o.Id == id);
        if (inst is null) return;
        // Select it, then open the modal — reuses OpenObjectEditor's
        // "edit the currently-selected instance" entry point.
        OnObjectSingleSelectRequested(id);
        await OpenObjectEditor();
    }

    private void OnObjectPlaceRequested(int x, int y)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;
        var inst = new L22.Editor.Models.Objects.ObjectInstance
        {
            Id = Guid.NewGuid().ToString("N"),
            PrefabId = PrefabForNewObject(),
            CellX = x,
            CellY = y,
        };
        var cmd = new L22.Editor.Models.Commands.AddObjectCommand(ctx, inst);
        ctx.History.ExecuteAndPush(cmd);
    }

    private void OnCellSampled(int x, int y)
    {
        var ctx = ViewModel.Active?.Context;
        var brush = ViewModel.Brush;
        if (ctx is null || brush is null) return;
        if (brush.ActiveLayer == LayerKind.Elevation)
        {
            brush.ElevationValue = ctx.World.GetElevation(x, y);
        }
        else
        {
            var sampled = ctx.World.GetTerrainType(x, y);
            if (sampled != 0) brush.TerrainType = sampled;
        }
        // Reflect the picked value in the right-panel sliders / buttons.
        RefreshBrushBindings();
    }

    private void OnCornerOverride(object? s, RoutedEventArgs e) => ApplyCornerEdit(CornerApplyMode.Override);
    private void OnCornerAdd(object? s, RoutedEventArgs e)      => ApplyCornerEdit(CornerApplyMode.Add);

    private void ApplyCornerEdit(CornerApplyMode mode)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;
        if (EditViewport.SelectedCorners.Count == 0) return;
        if (!float.TryParse(TxtCornerValue.Text, out var val)) return;

        var cmd = new CornerElevationCommand();
        foreach (var (cx, cy) in EditViewport.SelectedCorners)
            cmd.Record(ctx.World, cx, cy, mode, val);
        if (cmd.Count > 0) ctx.History.PushExecuted(cmd);

        // Journey 003: selection stays after a change.
        UpdateCornerPanel();
        EditViewport.RequestFrame();
        FreeViewport.RequestFrame();
    }

    private void UpdateCornerPanel()
    {
        LblCornerSelCount.Text = $"{EditViewport.SelectedCorners.Count} corner{(EditViewport.SelectedCorners.Count == 1 ? "" : "s")} selected";
    }

    private void OnRadiusChanged(object? s, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var v = (int)Math.Round(e.NewValue);
        if (ViewModel.Brush is { } b) b.Radius = v;
        if (LblRadius != null) LblRadius.Text = v.ToString();
    }

    private void OnStrengthChanged(object? s, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.Brush is { } b) b.Strength = (float)e.NewValue;
        if (LblStrength != null) LblStrength.Text = e.NewValue.ToString("0.00");
    }

    private void OnElevChanged(object? s, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.Brush is { } b) b.ElevationValue = (float)e.NewValue;
        if (LblElevation != null) LblElevation.Text = e.NewValue.ToString("0.00");
    }

    // ---- Slider inline edit ----

    private void RegisterSliderBinding(Slider slider, TextBlock label, TextBox editor, Action<double> apply, Func<double, string> format)
    {
        var b = new SliderBinding(slider, label, editor, apply, format);
        _sliderBindings[slider] = b;
        _editorBindings[editor] = b;
    }

    private void OnSliderHover(object? s, PointerEventArgs e)
    {
        if (s is Slider sl) _hoveredSlider = sl;
    }

    private void OnSliderExit(object? s, PointerEventArgs e)
    {
        if (ReferenceEquals(s, _hoveredSlider)) _hoveredSlider = null;
    }

    private void BeginInlineEdit(Slider slider, string initial)
    {
        if (!_sliderBindings.TryGetValue(slider, out var b)) return;
        b.Editor.Text = initial;
        b.Editor.CaretIndex = b.Editor.Text.Length;
        b.Label.IsVisible = false;
        b.Editor.IsVisible = true;
        b.Editor.Focus();
    }

    private void OnSliderEditKey(object? s, KeyEventArgs e)
    {
        if (s is not TextBox tb || !_editorBindings.TryGetValue(tb, out var b)) return;
        if (e.Key == Key.Enter)  { CommitInlineEdit(b, keepValue: true);  e.Handled = true; }
        else if (e.Key == Key.Escape) { CommitInlineEdit(b, keepValue: false); e.Handled = true; }
    }

    private void OnSliderEditLost(object? s, RoutedEventArgs e)
    {
        if (s is not TextBox tb || !_editorBindings.TryGetValue(tb, out var b)) return;
        if (!b.Editor.IsVisible) return;
        CommitInlineEdit(b, keepValue: true);
    }

    private void CommitInlineEdit(SliderBinding b, bool keepValue)
    {
        if (keepValue && double.TryParse(b.Editor.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            v = Math.Clamp(v, b.Slider.Minimum, b.Slider.Maximum);
            b.Slider.Value = v;
            b.Label.Text = b.Format(v);
            b.Apply(v);
        }
        b.Editor.IsVisible = false;
        b.Label.IsVisible = true;
    }

    private static string? NumericCharFromKey(Key k)
    {
        if (k >= Key.D0 && k <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
        if (k >= Key.NumPad0 && k <= Key.NumPad9) return ((char)('0' + (k - Key.NumPad0))).ToString();
        if (k == Key.OemPeriod || k == Key.Decimal) return ".";
        if (k == Key.OemMinus || k == Key.Subtract) return "-";
        return null;
    }

    private static bool IsSourceInsideTextBox(RoutedEventArgs e)
    {
        var v = e.Source as Visual;
        while (v is not null)
        {
            if (v is TextBox) return true;
            v = v.GetVisualParent();
        }
        return false;
    }

    private void OnPickSand(object? s, RoutedEventArgs e)  => ViewModel.PickTerrainType(TerrainType.Sand);
    private void OnPickGrass(object? s, RoutedEventArgs e) => ViewModel.PickTerrainType(TerrainType.Grass);
    private void OnPickRock(object? s, RoutedEventArgs e)  => ViewModel.PickTerrainType(TerrainType.Rock);

    public void SetServerStatus(string text)
    {
        if (LblServerStatus != null) LblServerStatus.Text = text;
    }

    // Called by the IPC admin ops (new-scene / new-prefab).
    public void CreateNewScene()  => CreateAndSelect(DocumentKind.Scene);
    public void CreateNewPrefab() => CreateAndSelect(DocumentKind.Prefab);
}
