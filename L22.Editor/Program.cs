using System;
using Avalonia;

namespace L22.Editor;

class Program
{
    public static CliArgs Args { get; private set; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        Args = CliArgs.Parse(args);
        if (Args.VerifyIoMode) return IoVerify.Run();
        if (Args.NewPath is { } np) return RunNew(np, Args.TemplateName, Args.ForceOverwrite);
        // Seed a document for the sample-mound verify flow. The new-scene
        // verify flow deliberately starts empty so it exercises the actual
        // welcome-screen → New Scene path.
        if (Args.VerifyMode) EditorContext.CreateDefault(Args.FileId);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[fatal] {ex}");
            return 1;
        }
        return App.ExitCode;
    }

    // Headless project creation. Returns 0 on success, 1 on error. Prints
    // the final path on stdout so a wrapping script can pick it up.
    private static int RunNew(string path, string? template, bool force)
    {
        try
        {
            if (System.IO.File.Exists(path) && !force)
            {
                Console.Error.WriteLine($"refusing to overwrite existing file: {path} (pass --force to replace)");
                return 1;
            }
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

            var fileId = System.IO.Path.GetFileNameWithoutExtension(path);
            var world = new Models.World.World(fileId);
            System.Collections.Generic.List<Models.Objects.ObjectInstance> objects;
            switch (template?.ToLowerInvariant())
            {
                case null:
                case "":
                case "empty":         Templates.FillEmpty(world, out objects); break;
                case "desert-island": Templates.FillDesertIsland(world, out objects); break;
                default:
                    Console.Error.WriteLine($"unknown template '{template}'. known: empty, desert-island");
                    return 1;
            }

            Models.World.ProjectIO.Save(world, Models.DocumentKind.Scene, path, objects);
            Console.Out.WriteLine(System.IO.Path.GetFullPath(path));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"--new failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
