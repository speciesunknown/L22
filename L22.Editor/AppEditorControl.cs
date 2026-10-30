using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using L22.Editor.Models.Ipc;
using L22.Editor.Views;

namespace L22.Editor;

// Bridge between the IPC server and the UI. Lives in the App assembly so it
// can reach the MainWindow and ViewModel; the server itself depends only on
// the IEditorControl abstraction.
public sealed class AppEditorControl : IEditorControl
{
    private readonly App _app;

    public AppEditorControl(App app) { _app = app; }

    public Task<string> NewScene() => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var main = _app.MainWindowRef;
        if (main is null) return "(no window)";
        main.CreateNewScene();
        return main.ViewModel.Active?.Context.World.FileId ?? "(no doc)";
    }).GetTask();

    public Task<string> NewPrefab() => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var main = _app.MainWindowRef;
        if (main is null) return "(no window)";
        main.CreateNewPrefab();
        return main.ViewModel.Active?.Context.World.FileId ?? "(no doc)";
    }).GetTask();

    public async Task<(int w, int h)> Screenshot(string viewportName, string path)
    {
        var main = _app.MainWindowRef;
        if (main is null) return (0, 0);

        TerrainViewport? vp = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vp = viewportName.ToLowerInvariant() switch
            {
                "free" or "preview" => main.FindNameScope()?.Find<TerrainViewport>("FreeViewport"),
                _ => main.FindNameScope()?.Find<TerrainViewport>("EditViewport"),
            };
        });
        if (vp is null) return (0, 0);

        var tcs = new TaskCompletionSource<(int, int)>();
        void OnDone()
        {
            vp!.CaptureCompleted -= OnDone;
            if (vp.LastCaptureRgba is null) { tcs.SetResult((0, 0)); return; }
            try
            {
                PngWriter.WriteRgba(path, vp.LastCaptureRgba, vp.LastCaptureWidth, vp.LastCaptureHeight, flipY: true);
                tcs.SetResult((vp.LastCaptureWidth, vp.LastCaptureHeight));
            }
            catch { tcs.SetResult((0, 0)); }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vp.CaptureCompleted += OnDone;
            vp.CaptureRequested = true;
            vp.RequestFrame();
        });

        return await tcs.Task;
    }

    public IEnumerable<DocumentInfo> ListDocuments()
    {
        var main = _app.MainWindowRef;
        if (main is null) return System.Array.Empty<DocumentInfo>();
        var docs = new List<DocumentInfo>();
        foreach (var d in main.ViewModel.Documents)
            docs.Add(new DocumentInfo(d.Context.World.FileId, d.Name, d.Kind.ToString()));
        return docs;
    }

    public EditorContext? GetActiveContext()
    {
        var main = _app.MainWindowRef;
        return main?.ViewModel.Active?.Context;
    }
}

internal static class DispatcherExt
{
    // Avalonia 12's InvokeAsync returns a non-Task awaitable; adapt it so the
    // control implementation can return proper Tasks.
    public static Task<T> GetTask<T>(this Avalonia.Threading.DispatcherOperation<T> op)
    {
        var tcs = new TaskCompletionSource<T>();
        _ = Task.Run(async () =>
        {
            try { tcs.SetResult(await op); }
            catch (System.Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
