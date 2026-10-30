using System;

namespace L22.Editor;

public sealed class CliArgs
{
    public string FileId { get; private set; } = "default";
    public int Port { get; private set; } = 47622;

    // Verification mode: paint a sample scene, capture edit viewport as PNG,
    // exit the moment the frame is written.
    public bool VerifyMode { get; private set; }
    public string? VerifyOutputPath { get; private set; }
    public int VerifyWidth { get; private set; } = 1024;
    public int VerifyHeight { get; private set; } = 640;

    // Headless IO round-trip: build a World, save, reload, cell-by-cell compare.
    // Prints OK and exits 0, or FAIL + reason and exits 1. No GUI.
    public bool VerifyIoMode { get; private set; }

    // Test mode: any agent connection is auto-approved (no PIN dialog) and
    // the status label flashes a YOLO banner so you don't forget. Intended
    // for driving the editor from an audit agent.
    public bool Yolo { get; private set; }

    public static CliArgs Parse(string[] args)
    {
        var r = new CliArgs();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--file":
                case "--file-id":
                    r.FileId = args[++i];
                    break;
                case "--port":
                    r.Port = int.Parse(args[++i]);
                    break;
                case "--verify":
                    r.VerifyMode = true;
                    r.VerifyOutputPath = args[++i];
                    break;
                case "--verify-io":
                    r.VerifyIoMode = true;
                    break;
                case "--yolo":
                    r.Yolo = true;
                    break;
                case "--verify-size":
                {
                    var parts = args[++i].Split('x');
                    r.VerifyWidth = int.Parse(parts[0]);
                    r.VerifyHeight = int.Parse(parts[1]);
                    break;
                }
                case "--help":
                case "-h":
                    Console.WriteLine(Usage());
                    Environment.Exit(0);
                    break;
            }
        }
        return r;
    }

    public static string Usage() => """
        L22.Editor options:
          --file <id>              File ID agents target. Default: default
          --port <n>               IPC listen port on 127.0.0.1. Default: 47622
          --verify <png>           Headless: paint sample scene, write PNG, exit
          --verify-size WxH        Size hint for verify render (default 1024x640)
          --verify-io              Headless: save/load round-trip test, no GUI
          --yolo                   Auto-approve agent connections (test mode).
                                   Agents use IPC verbs new-scene, screenshot,
                                   list-docs, paint, undo, redo.
        """;
}
