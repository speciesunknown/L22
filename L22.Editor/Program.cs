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

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
