using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace L22.Editor.Models.Ipc;

public sealed class IpcServer
{
    private readonly int _port;
    private readonly PinManager _pins = new();
    private readonly Func<string, EditorContext?> _resolveContext;
    private readonly Func<string, string, Task<ApprovalResult>> _promptUser;
    private readonly IEditorControl _control;
    private readonly Action<string> _log;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public int Port => _port;
    public bool Running => _listener is not null;

    public IpcServer(int port,
                     Func<string, EditorContext?> resolveContext,
                     Func<string, string, Task<ApprovalResult>> promptUser,
                     IEditorControl control,
                     Action<string> log)
    {
        _port = port;
        _resolveContext = resolveContext;
        _promptUser = promptUser;
        _control = control;
        _log = log;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        _log($"IPC server listening on 127.0.0.1:{_port}");
        _ = Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        var sessions = new List<Task>();
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                var session = new AgentSession(client, _pins, _resolveContext, _promptUser, _control, _log);
                sessions.Add(session.RunAsync(ct));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log($"accept error: {ex.Message}"); }
        }
        try { await Task.WhenAll(sessions); } catch { }
    }
}
