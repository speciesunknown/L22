using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using L22.Editor.Models.Commands;
using L22.Editor.Models.World;

namespace L22.Editor.Models.Ipc;

public sealed class AgentSession
{
    private readonly TcpClient _client;
    private readonly PinManager _pins;
    private readonly Func<string, EditorContext?> _resolveContext;
    private readonly Func<string, string, Task<ApprovalResult>> _promptUser;
    private readonly IEditorControl _control;
    private readonly Action<string> _log;

    private const string AdminWildcard = "*";

    public AgentSession(TcpClient client, PinManager pins,
                        Func<string, EditorContext?> resolveContext,
                        Func<string, string, Task<ApprovalResult>> promptUser,
                        IEditorControl control,
                        Action<string> log)
    {
        _client = client;
        _pins = pins;
        _resolveContext = resolveContext;
        _promptUser = promptUser;
        _control = control;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var ep = _client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _log($"agent connected from {ep}");
        try
        {
            using var stream = _client.GetStream();
            using var reader = new StreamReader(stream);
            using var writer = new StreamWriter(stream) { AutoFlush = true };

            // --- handshake ---
            var helloLine = await reader.ReadLineAsync(ct);
            if (helloLine is null) return;
            var hello = JsonSerializer.Deserialize<HelloRequest>(helloLine);
            if (hello is null)
            {
                await SendAsync(writer, new ServerResponse { Op = "error", Msg = "bad hello" });
                return;
            }

            // "*" is the admin session: no specific document required. Admin
            // sessions can create/list documents and screenshot; doc ops fall
            // through to whichever document is currently active.
            bool isAdmin = hello.FileId == AdminWildcard || string.IsNullOrEmpty(hello.FileId);
            var ctx = isAdmin ? _control.GetActiveContext() : _resolveContext(hello.FileId);
            if (!isAdmin && ctx is null)
            {
                await SendAsync(writer, new ServerResponse { Op = "error", Msg = $"unknown fileId '{hello.FileId}'" });
                return;
            }

            var ipAddr = (_client.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? ep;
            var agentName = string.IsNullOrEmpty(hello.AgentName) ? "unnamed" : hello.AgentName;
            var agentKey = $"{ipAddr}|{agentName}";

            TimeSpan grant;
            string? issuedToken = null;

            // Three auth paths:
            //   1. Hello carries a valid persistent token → immediate accept.
            //   2. Hello carries a PIN → match against a pending auth that
            //      the dialog on step 3 set up on a previous connection.
            //   3. Hello has neither → pop dialog on the user's screen with a
            //      freshly generated 6-digit PIN, store the result as pending,
            //      and send pin-required back. The agent asks the human for
            //      the PIN and reconnects with it.
            if (!string.IsNullOrEmpty(hello.Token) && _pins.IsTokenValid(hello.Token))
            {
                _log($"agent {ep} ({agentName}) accepted via token");
                grant = TimeSpan.FromMinutes(60);  // best-effort hint
            }
            else if (!string.IsNullOrEmpty(hello.Pin))
            {
                if (!_pins.TryConsumePending(agentKey, hello.Pin, out var decision, out var duration))
                {
                    await SendAsync(writer, new ServerResponse { Op = "error", Msg = "pin invalid, expired, or no pending auth" });
                    return;
                }
                if (decision == ApprovalDecision.Block)
                {
                    await SendAsync(writer, new ServerResponse { Op = "blocked" });
                    return;
                }
                grant = duration;
                issuedToken = _pins.IssueToken(duration);
                _log($"agent {ep} ({agentName}) accepted via PIN, token issued ({duration.TotalHours}h)");
            }
            else
            {
                var approval = await _promptUser(hello.FileId, $"{agentName} @ {ipAddr}");
                if (approval.Decision == ApprovalDecision.Block)
                {
                    await SendAsync(writer, new ServerResponse { Op = "blocked" });
                    return;
                }
                var dur = TimeSpan.FromHours(approval.Decision == ApprovalDecision.AllowOneHour ? 1 : 24);
                if (approval.BypassPin)
                {
                    // Yolo / test mode — skip the PIN round-trip entirely and
                    // issue a token straight away.
                    grant = dur;
                    issuedToken = _pins.IssueToken(dur);
                    _log($"agent {ep} ({agentName}) accepted via bypass, token issued");
                }
                else
                {
                    _pins.SavePending(agentKey, approval.Pin, approval.Decision, dur);
                    _log($"agent {ep} ({agentName}) pin pending (user allowed, waiting for PIN exchange)");
                    await SendAsync(writer, new ServerResponse
                    {
                        Op = "pin-required",
                        Msg = "Ask the human for the PIN shown on the editor, then reconnect with --pin <N>",
                    });
                    return;
                }
            }

            await SendAsync(writer, new ServerResponse
            {
                Op = "accepted",
                DurationHours = (int)Math.Round(grant.TotalHours),
                Token = issuedToken,
            });

            // --- command loop ---
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                CommandRequest? cmd;
                try { cmd = JsonSerializer.Deserialize<CommandRequest>(line); }
                catch (Exception ex)
                {
                    await SendAsync(writer, new ServerResponse { Op = "error", Msg = ex.Message });
                    continue;
                }
                if (cmd is null) continue;

                var resp = await ExecuteOnUiThread(isAdmin, cmd);
                await SendAsync(writer, resp);
            }
        }
        catch (Exception ex)
        {
            _log($"agent {ep} error: {ex.Message}");
        }
        finally
        {
            _log($"agent {ep} disconnected");
            try { _client.Close(); } catch { }
        }
    }

    private async Task<ServerResponse> ExecuteOnUiThread(bool isAdmin, CommandRequest cmd)
    {
        switch (cmd.Op)
        {
            // --- Admin verbs (no doc context required) -----------------
            case "new-scene":
            {
                var fileId = await _control.NewScene();
                return new ServerResponse { Op = "ok", FileId = fileId };
            }
            case "new-prefab":
            {
                var fileId = await _control.NewPrefab();
                return new ServerResponse { Op = "ok", FileId = fileId };
            }
            case "list-docs":
            {
                var docs = _control.ListDocuments().ToArray();
                return new ServerResponse { Op = "ok", Documents = docs };
            }
            case "screenshot":
            {
                var viewport = cmd.Viewport ?? "edit";
                var path = cmd.Path;
                if (string.IsNullOrEmpty(path))
                    return new ServerResponse { Op = "error", Msg = "screenshot requires 'path'" };
                var (w, h) = await _control.Screenshot(viewport, path);
                if (w == 0) return new ServerResponse { Op = "error", Msg = $"capture failed on viewport '{viewport}'" };
                return new ServerResponse { Op = "ok", Width = w, Height = h, Path = path };
            }
            case "ping":
                return new ServerResponse { Op = "pong" };

            // --- Doc ops --------------------------------------------------
            case "paint":
            case "undo":
            case "redo":
            case "setBrush":
            case "object-list":
            case "object-place":
            case "object-delete":
            {
                var ctx = _control.GetActiveContext();
                if (ctx is null) return new ServerResponse { Op = "error", Msg = "no active document" };
                return await Dispatcher.UIThread.InvokeAsync(() => ExecuteDocCommand(ctx, cmd));
            }

            default:
                return new ServerResponse { Op = "error", Msg = $"unknown op '{cmd.Op}'" };
        }
    }

    private static ServerResponse ExecuteDocCommand(EditorContext ctx, CommandRequest cmd)
    {
        switch (cmd.Op)
        {
            case "paint":
                return ExecutePaint(ctx, cmd);
            case "undo":
                ctx.History.Undo();
                return new ServerResponse { Op = "ok" };
            case "redo":
                ctx.History.Redo();
                return new ServerResponse { Op = "ok" };
            case "setBrush":
                if (cmd.Radius.HasValue) ctx.Brush.Radius = cmd.Radius.Value;
                if (cmd.Value.HasValue) ctx.Brush.Strength = (float)cmd.Value.Value;
                return new ServerResponse { Op = "ok" };
            case "object-list":
                return ExecuteObjectList(ctx);
            case "object-place":
                return ExecuteObjectPlace(ctx, cmd);
            case "object-delete":
                return ExecuteObjectDelete(ctx, cmd);
        }
        return new ServerResponse { Op = "error", Msg = $"unknown doc op '{cmd.Op}'" };
    }

    private static ServerResponse ExecuteObjectList(EditorContext ctx)
    {
        var arr = new ObjectInfo[ctx.Objects.Count];
        for (int i = 0; i < ctx.Objects.Count; i++)
        {
            var o = ctx.Objects[i];
            arr[i] = new ObjectInfo(o.Id, o.PrefabId, o.CellX, o.CellY, o.YOffsetWorld);
        }
        return new ServerResponse { Op = "ok", Objects = arr };
    }

    private static ServerResponse ExecuteObjectPlace(EditorContext ctx, CommandRequest cmd)
    {
        if (!cmd.CellX.HasValue || !cmd.CellY.HasValue)
            return new ServerResponse { Op = "error", Msg = "object-place requires cellX,cellY" };
        var inst = new Models.Objects.ObjectInstance
        {
            Id = string.IsNullOrEmpty(cmd.Id) ? System.Guid.NewGuid().ToString("N") : cmd.Id!,
            PrefabId = cmd.PrefabId ?? "none",
            CellX = cmd.CellX.Value,
            CellY = cmd.CellY.Value,
            YOffsetWorld = (float)(cmd.YOffsetWorld ?? 0.0),
        };
        var addCmd = new Models.Commands.AddObjectCommand(ctx, inst);
        ctx.History.ExecuteAndPush(addCmd);
        return new ServerResponse { Op = "ok", Id = inst.Id };
    }

    private static ServerResponse ExecuteObjectDelete(EditorContext ctx, CommandRequest cmd)
    {
        if (string.IsNullOrEmpty(cmd.Id))
            return new ServerResponse { Op = "error", Msg = "object-delete requires id" };
        var inst = ctx.Objects.Find(o => o.Id == cmd.Id);
        if (inst is null)
            return new ServerResponse { Op = "error", Msg = $"no object with id '{cmd.Id}'" };
        var del = new Models.Commands.DeleteObjectCommand(ctx, inst);
        ctx.History.ExecuteAndPush(del);
        return new ServerResponse { Op = "ok", Id = inst.Id };
    }

    private static ServerResponse ExecutePaint(EditorContext ctx, CommandRequest cmd)
    {
        if (!cmd.X.HasValue || !cmd.Y.HasValue)
            return new ServerResponse { Op = "error", Msg = "paint requires x,y" };
        int radius = cmd.Radius ?? Math.Max(1, ctx.Brush.Radius);
        string layer = (cmd.Layer ?? "elevation").ToLowerInvariant();

        if (layer == "elevation")
        {
            var paint = new ElevationPaintCommand();
            float target = (float)Math.Clamp(cmd.Value ?? 0.75, 0.0, 1.0);
            StampDisk(cmd.X.Value, cmd.Y.Value, radius, (x, y, w) =>
            {
                float cur = ctx.World.GetElevation(x, y);
                float next = Math.Clamp(cur + (target - cur) * w, 0f, 1f);
                paint.Record(ctx.World, x, y, next);
            });
            if (paint.Count > 0) ctx.History.PushExecuted(paint);
            return new ServerResponse { Op = "ok" };
        }
        else if (layer == "terraintype" || layer == "terrain" || layer == "type")
        {
            byte type = cmd.TerrainType?.ToLowerInvariant() switch
            {
                "sand" => (byte)TerrainType.Sand,
                "grass" => (byte)TerrainType.Grass,
                "rock" => (byte)TerrainType.Rock,
                _ => (byte)(int)Math.Clamp(cmd.Value ?? 2, 0, 3),
            };
            var paint = new TerrainTypePaintCommand();
            StampDisk(cmd.X.Value, cmd.Y.Value, radius, (x, y, w) =>
            {
                if (w >= 0.5f) paint.Record(ctx.World, x, y, type);
            });
            if (paint.Count > 0) ctx.History.PushExecuted(paint);
            return new ServerResponse { Op = "ok" };
        }

        return new ServerResponse { Op = "error", Msg = $"unknown layer '{cmd.Layer}'" };
    }

    private static void StampDisk(int cx, int cy, int radius, Action<int, int, float> stamp)
    {
        int r2 = radius * radius;
        for (int dy = -radius + 1; dy < radius; dy++)
        for (int dx = -radius + 1; dx < radius; dx++)
        {
            int d2 = dx * dx + dy * dy;
            if (d2 >= r2) continue;
            float t = 1f - MathF.Sqrt(d2) / radius;
            stamp(cx + dx, cy + dy, MathF.Pow(t, 1.5f));
        }
    }

    private static async Task SendAsync(StreamWriter w, ServerResponse resp)
    {
        var json = JsonSerializer.Serialize(resp);
        await w.WriteLineAsync(json);
    }
}

public enum ApprovalDecision { Block, AllowOneHour, AllowTwentyFourHours }

public sealed record ApprovalResult(ApprovalDecision Decision, string Pin, bool BypassPin = false);
