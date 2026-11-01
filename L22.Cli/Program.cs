using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

// L22.Cli: thin client that talks JSON Lines over TCP to a running L22.Editor.
//
// Usage:
//   l22 ping              --file <id> --pin <pin> [--host 127.0.0.1 --port 47622]
//   l22 paint             --file <id> --pin <pin> --layer elevation|terrainType
//                         --x N --y N [--radius R] [--value 0..1] [--type sand|grass|rock]
//   l22 undo              --file <id> --pin <pin>
//   l22 redo              --file <id> --pin <pin>
//   l22 set-brush         --file <id> --pin <pin> [--radius R] [--value 0..1]

namespace L22.Cli;

static class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 2; }
        var verb = args[0].ToLowerInvariant();
        var kv = ParseFlags(args.AsSpan(1));

        string host = kv.GetValueOrDefault("--host", "127.0.0.1");
        int port = int.Parse(kv.GetValueOrDefault("--port", "47622"));
        string file = kv.GetValueOrDefault("--file", "default");
        string pin = kv.GetValueOrDefault("--pin", "");
        string token = kv.GetValueOrDefault("--token", "");
        string agentName = kv.GetValueOrDefault("--name", Environment.UserName ?? "cli");

        using var client = new TcpClient();
        try { await client.ConnectAsync(host, port); }
        catch (Exception ex) { Console.Error.WriteLine($"connect failed: {ex.Message}"); return 3; }

        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream);
        var writer = new StreamWriter(stream) { AutoFlush = true };

        var hello = new { op = "hello", fileId = file, pin, token, agentName };
        await writer.WriteLineAsync(JsonSerializer.Serialize(hello));
        var helloResp = await reader.ReadLineAsync();
        Console.WriteLine($"server: {helloResp}");
        if (helloResp is null) return 1;
        if (helloResp.Contains("\"blocked\"") || helloResp.Contains("\"error\"")) return 1;
        if (helloResp.Contains("\"pin-required\""))
        {
            Console.Error.WriteLine(
                "The editor is showing a 6-digit PIN. Ask the human for it, " +
                "then rerun this verb with --pin <PIN>. On success you'll be " +
                "given a persistent --token to use for future calls.");
            return 2;
        }

        object? req = verb switch
        {
            "ping" => new { op = "ping" },
            "undo" => new { op = "undo" },
            "redo" => new { op = "redo" },
            "new-scene"  => new { op = "new-scene" },
            "new-prefab" => new { op = "new-prefab" },
            "list-docs"  => new { op = "list-docs" },
            "screenshot" => new
            {
                op = "screenshot",
                viewport = kv.GetValueOrDefault("--viewport", "edit"),
                path = kv.GetValueOrDefault("--path", ""),
            },
            "set-brush" => new
            {
                op = "setBrush",
                radius = kv.ContainsKey("--radius") ? int.Parse(kv["--radius"]) : (int?)null,
                value = kv.ContainsKey("--value") ? double.Parse(kv["--value"]) : (double?)null,
            },
            "paint" => new
            {
                op = "paint",
                layer = kv.GetValueOrDefault("--layer", "elevation"),
                x = int.Parse(kv["--x"]),
                y = int.Parse(kv["--y"]),
                radius = kv.ContainsKey("--radius") ? int.Parse(kv["--radius"]) : (int?)null,
                value = kv.ContainsKey("--value") ? double.Parse(kv["--value"]) : (double?)null,
                type = kv.GetValueOrDefault("--type"),
            },
            "object-list" => new { op = "object-list" },
            "object-place" => new
            {
                op = "object-place",
                id = kv.GetValueOrDefault("--id"),
                prefabId = kv.GetValueOrDefault("--prefab", "none"),
                cellX = int.Parse(kv["--cell-x"]),
                cellY = int.Parse(kv["--cell-y"]),
                yOffsetWorld = kv.ContainsKey("--y-offset") ? double.Parse(kv["--y-offset"]) : (double?)null,
            },
            "object-delete" => new
            {
                op = "object-delete",
                id = kv["--id"],
            },
            "prefab-list" => new { op = "prefab-list" },
            "prefab-upsert" => new
            {
                op = "prefab-upsert",
                id = kv.GetValueOrDefault("--id"),
                displayName = kv.GetValueOrDefault("--display-name", ""),
                foreignId = kv.GetValueOrDefault("--foreign-id", ""),
            },
            "prefab-delete" => new
            {
                op = "prefab-delete",
                id = kv["--id"],
            },
            "river-list" => new { op = "river-list" },
            "river-create" => new
            {
                op = "river-create",
                id = kv.GetValueOrDefault("--id"),
                name = kv.GetValueOrDefault("--name"),
            },
            "river-add-vertex" => new
            {
                op = "river-add-vertex",
                id = kv["--id"],
                worldX = double.Parse(kv["--world-x"], System.Globalization.CultureInfo.InvariantCulture),
                worldZ = double.Parse(kv["--world-z"], System.Globalization.CultureInfo.InvariantCulture),
                width = kv.ContainsKey("--width") ? double.Parse(kv["--width"], System.Globalization.CultureInfo.InvariantCulture) : (double?)null,
            },
            "river-delete" => new
            {
                op = "river-delete",
                id = kv["--id"],
            },
            _ => null,
        };
        if (req is null) { Console.Error.WriteLine($"unknown verb '{verb}'"); PrintUsage(); return 2; }

        await writer.WriteLineAsync(JsonSerializer.Serialize(req));
        var resp = await reader.ReadLineAsync();
        Console.WriteLine($"server: {resp}");
        return 0;
    }

    static Dictionary<string, string> ParseFlags(ReadOnlySpan<string> a)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) continue;
            string key = a[i];
            string val = (i + 1 < a.Length && !a[i + 1].StartsWith("--")) ? a[++i] : "";
            d[key] = val;
        }
        return d;
    }

    static void PrintUsage() => Console.WriteLine(
        """
        L22.Cli -- talk to a running L22.Editor over TCP.

        Common flags for every verb:
          --host 127.0.0.1   --port 47622
          --file <id>        --name <label>
          --pin <6-digit>    (first-time connection, after human reads editor dialog)
          --token <hex>      (persistent code returned on previous PIN exchange)

        Use --file "*" for an admin session. Doc ops in admin mode act on the
        currently active document. In --yolo mode on the editor, PIN/token
        are skipped.

        Admin verbs:
          new-scene
          new-prefab
          list-docs
          screenshot --viewport edit|free --path <absolute path>

        Doc verbs:
          ping
          paint --layer elevation --x N --y N [--radius R] [--value 0..1]
          paint --layer terrainType --x N --y N [--radius R] --type sand|grass|rock
          undo
          redo
          set-brush [--radius R] [--value 0..1]
          object-list
          object-place --cell-x N --cell-y N [--prefab ID] [--id ID] [--y-offset Y]
          object-delete --id ID
          prefab-list
          prefab-upsert [--id ID] [--display-name NAME] [--foreign-id KEY]
          prefab-delete --id ID
          river-list
          river-create [--id ID] [--name NAME]
          river-add-vertex --id RIVER_ID --world-x X --world-z Z [--width W]
          river-delete --id ID
        """);
}
