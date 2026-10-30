using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using L22.Editor.Models.Commands;
using L22.Editor.Models.Ipc;
using L22.Editor.Views;
using WorldNs = L22.Editor.Models.World;

namespace L22.Editor;

public partial class App : Application
{
    public static int ExitCode { get; set; }

    private IpcServer? _server;
    private MainWindow? _main;

    public MainWindow? MainWindowRef => _main;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _main = new MainWindow();
            desktop.MainWindow = _main;

            StartIpcServer();

            if (Program.Args.VerifyMode)
            {
                _main.Width = Program.Args.VerifyWidth;
                _main.Height = Program.Args.VerifyHeight;
                _main.Opened += (_, _) => Dispatcher.UIThread.Post(RunVerifyCapture, DispatcherPriority.Background);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void StartIpcServer()
    {
        _server = new IpcServer(Program.Args.Port,
            resolveContext: id =>
            {
                if (_main?.ViewModel is null) return null;
                foreach (var d in _main.ViewModel.Documents)
                    if (d.Context.World.FileId == id) return d.Context;
                return null;
            },
            promptUser: PromptUserForApproval,
            control: new AppEditorControl(this),
            log: msg => Console.Error.WriteLine($"[ipc] {msg}"));
        try
        {
            _server.Start();
            var prefix = Program.Args.Yolo ? "YOLO " : "";
            _main?.SetServerStatus($"{prefix}Server: 127.0.0.1:{_server.Port}");
        }
        catch (Exception ex)
        {
            _main?.SetServerStatus($"Server failed: {ex.Message}");
        }
    }

    private async Task<ApprovalResult> PromptUserForApproval(string fileId, string agent)
    {
        if (Program.Args.Yolo)
            return new ApprovalResult(ApprovalDecision.AllowTwentyFourHours, "", BypassPin: true);

        var tcs = new TaskCompletionSource<ApprovalResult>();
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var dlg = new PinApprovalDialog(fileId, agent);
            if (_main is not null)
                await dlg.ShowDialog(_main);
            else
                dlg.Show();
            tcs.SetResult(new ApprovalResult(dlg.Result, dlg.GeneratedPin));
        });
        return await tcs.Task;
    }

    private void RunVerifyCapture()
    {
        if (_main is null) return;
        var ctx = EditorContext.Current;
        if (ctx is null) { Program.Args.PrintErrorAndExit("no active document"); return; }

        // Paint a reproducible sample scene so the capture is comparable
        // across runs.
        PaintSampleScene(ctx);

        var vp = _main.FindControl<TerrainViewport>("EditViewport");
        if (vp is null) { Program.Args.PrintErrorAndExit("edit viewport missing"); return; }

        // Render the sample as a perspective colored scene so the capture
        // exercises both layers (otherwise the default Elevation layer puts
        // the viewport into greyscale mode and the colors are invisible).
        vp.TopDown = false;
        vp.ShowElevationGreyscale = false;
        vp.Camera.IsOrthographic = false;

        // Position the camera so the painted mound at (64, 64) is clearly
        // framed from the south-east, looking back toward the peak.
        vp.Camera.Position = new System.Numerics.Vector3(118, 22, 118);
        vp.Camera.Yaw = -0.78f;
        vp.Camera.Pitch = -0.28f;

        vp.CaptureCompleted += OnCaptureDone;
        vp.CaptureRequested = true;
        vp.RequestFrame();
    }

    private void OnCaptureDone()
    {
        var vp = _main?.FindControl<TerrainViewport>("EditViewport");
        if (vp?.LastCaptureRgba is null)
        {
            Program.Args.PrintErrorAndExit("capture failed");
            return;
        }
        try
        {
            PngWriter.WriteRgba(Program.Args.VerifyOutputPath!, vp.LastCaptureRgba,
                                vp.LastCaptureWidth, vp.LastCaptureHeight, flipY: true);
            Console.WriteLine($"wrote {Program.Args.VerifyOutputPath} ({vp.LastCaptureWidth}x{vp.LastCaptureHeight})");
            ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"png write failed: {ex}");
            ExitCode = 2;
        }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d) d.Shutdown();
    }

    private static void PaintSampleScene(EditorContext ctx)
    {
        // A heart-of-the-world mound of elevation with grass on top, sand
        // skirt and a rock peak. Chosen to exercise all three terrain types
        // and the elevation layer.
        var elev = new ElevationPaintCommand();
        for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            float dx = x - 64, dy = y - 64;
            float r = MathF.Sqrt(dx * dx + dy * dy);
            float v = MathF.Max(0, 1f - r / 45f);
            // Smootherstep gives a nice round shoulder on the mound.
            v = v * v * (3f - 2f * v);
            if (v > 0.001f) elev.Record(ctx.World, x, y, v);
        }
        if (elev.Count > 0) ctx.History.PushExecuted(elev);

        var type = new TerrainTypePaintCommand();
        for (int y = 0; y < 128; y++)
        for (int x = 0; x < 128; x++)
        {
            float dx = x - 64, dy = y - 64;
            float r = MathF.Sqrt(dx * dx + dy * dy);
            byte t = r < 10 ? (byte)WorldNs.TerrainType.Rock
                   : r < 32 ? (byte)WorldNs.TerrainType.Grass
                   : r < 48 ? (byte)WorldNs.TerrainType.Sand
                   : (byte)0;
            if (t != 0) type.Record(ctx.World, x, y, t);
        }
        if (type.Count > 0) ctx.History.PushExecuted(type);
    }
}

internal static class CliArgsExt
{
    public static void PrintErrorAndExit(this CliArgs _, string msg)
    {
        Console.Error.WriteLine(msg);
        App.ExitCode = 3;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
            d.Shutdown();
    }
}
