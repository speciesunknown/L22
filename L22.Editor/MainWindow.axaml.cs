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

        ViewModel.PropertyChanged += (_, _) => UpdateWindowTitle();
        UpdateWindowTitle();
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
        if (_objectsBoundContext is not null) _objectsBoundContext.ObjectsChanged -= RefreshObjectList;
        _objectsBoundContext = ctx;
        ctx.ObjectsChanged += RefreshObjectList;
        RefreshObjectList();
    }

    private void RefreshObjectList()
    {
        if (ObjectList is null) return;
        var ctx = ViewModel.Active?.Context;
        if (ctx is null)
        {
            ObjectList.ItemsSource = null;
            if (LblObjectCount is not null) LblObjectCount.Text = "0 objects";
            return;
        }
        var labels = new List<string>(ctx.Objects.Count);
        foreach (var o in ctx.Objects)
            labels.Add($"{o.Id}  ·  {o.PrefabId}  ·  ({o.CellX}, {o.CellY})");
        ObjectList.ItemsSource = labels;
        if (LblObjectCount is not null)
            LblObjectCount.Text = $"{ctx.Objects.Count} object" + (ctx.Objects.Count == 1 ? "" : "s");
    }

    private void OnObjectListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null || ObjectList is null || PropertyTree is null) return;
        int idx = ObjectList.SelectedIndex;
        if (idx < 0 || idx >= ctx.Objects.Count)
        {
            PropertyTree.ItemsSource = null;
            PanPropertyEditor.IsVisible = false;
            return;
        }
        var inst = ctx.Objects[idx];
        PropertyTree.ItemsSource = inst.PropertyOverrides;
        PanPropertyEditor.IsVisible = false;

        // Pan the top-down camera over the selected object. The 3D viewport
        // keeps its current position so the user can frame the shot themselves.
        EditViewport.Camera.Position = new System.Numerics.Vector3(
            inst.CellX + 0.5f,
            EditViewport.Camera.Position.Y,
            inst.CellY + 0.5f);
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
        // Reselecting the row re-syncs the tree's display line.
        var idx = ObjectList?.SelectedIndex ?? -1;
        if (idx >= 0 && idx < ctx.Objects.Count)
            PropertyTree.ItemsSource = ctx.Objects[idx].PropertyOverrides;
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
            PrefabId = "none",
            CellX = 64,
            CellY = 64,
        };
        var cmd = new L22.Editor.Models.Commands.AddObjectCommand(ctx, inst);
        ctx.History.ExecuteAndPush(cmd);
    }

    private void OnDeleteObjectClick(object? sender, RoutedEventArgs e)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null || ObjectList is null || ObjectList.SelectedIndex < 0) return;
        if (ObjectList.SelectedIndex >= ctx.Objects.Count) return;
        var inst = ctx.Objects[ObjectList.SelectedIndex];
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

        DropperOverlay.IsVisible   = wantDropper;
        BoxSelectOverlay.IsVisible = wantBox;
        BrushOverlay.IsVisible     = wantBrush;

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
        EditViewport.ClearSelectedCorners();
        EditViewportHost.Cursor = new Cursor(StandardCursorType.None);
        DropperOverlay.IsVisible = false;
        BoxSelectOverlay.IsVisible = false;
        UpdateLayerUi();
    }

    private void OnSelectToolCorners(object? s, RoutedEventArgs e)
    {
        if (ViewModel.Brush is { } b) b.ActiveTool = ToolKind.CornerElevation;
        EditViewport.CornerToolActive = true;
        EditViewport.DropperToolActive = false;
        EditViewport.PlaceObjectToolActive = false;
        // Hide the OS cursor and let the Avalonia Path overlay represent it.
        EditViewportHost.Cursor = new Cursor(StandardCursorType.None);
        UpdateLayerUi();
        EditViewport.RequestFrame();
    }

    private void OnSelectToolDropper(object? s, RoutedEventArgs e)
    {
        if (ViewModel.Brush is { } b) b.ActiveTool = ToolKind.Dropper;
        EditViewport.CornerToolActive = false;
        EditViewport.DropperToolActive = true;
        EditViewport.PlaceObjectToolActive = false;
        EditViewport.ClearSelectedCorners();
        EditViewportHost.Cursor = new Cursor(StandardCursorType.None);
        UpdateLayerUi();
    }

    private void OnSelectToolPlaceObject(object? s, RoutedEventArgs e)
    {
        if (ViewModel.Brush is { } b) b.ActiveTool = ToolKind.PlaceObject;
        EditViewport.CornerToolActive = false;
        EditViewport.DropperToolActive = false;
        EditViewport.PlaceObjectToolActive = true;
        EditViewport.ClearSelectedCorners();
        EditViewportHost.Cursor = new Cursor(StandardCursorType.Cross);
        UpdateLayerUi();
    }

    private void OnObjectPlaceRequested(int x, int y)
    {
        var ctx = ViewModel.Active?.Context;
        if (ctx is null) return;
        var inst = new L22.Editor.Models.Objects.ObjectInstance
        {
            Id = Guid.NewGuid().ToString("N"),
            PrefabId = "none",
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
