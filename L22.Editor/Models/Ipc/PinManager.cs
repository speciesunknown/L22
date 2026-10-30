using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace L22.Editor.Models.Ipc;

// Tracks three things:
//   1. Pending auths — a dialog has shown a PIN and the user clicked Allow,
//      the agent has yet to come back with the PIN. Expires in ~2 minutes.
//   2. Issued tokens — persistent hex codes an approved agent can present
//      on subsequent connections.
//   3. Historical approvals keyed by (fileId, agentKey, pin) so agents that
//      successfully round-tripped once don't re-prompt within the window.
public sealed class PinManager
{
    private readonly ConcurrentDictionary<string, DateTime> _approvedUntil = new();
    private readonly ConcurrentDictionary<string, PendingAuth> _pending = new();
    private readonly ConcurrentDictionary<string, DateTime> _tokens = new();

    public static TimeSpan PendingWindow { get; } = TimeSpan.FromMinutes(2);

    public sealed record PendingAuth(string Pin, ApprovalDecision Decision, TimeSpan Duration, DateTime Expires);

    public void SavePending(string agentKey, string pin, ApprovalDecision decision, TimeSpan duration)
    {
        _pending[agentKey] = new PendingAuth(pin, decision, duration, DateTime.UtcNow + PendingWindow);
    }

    public bool TryConsumePending(string agentKey, string pin, out ApprovalDecision decision, out TimeSpan duration)
    {
        decision = ApprovalDecision.Block;
        duration = TimeSpan.Zero;
        if (!_pending.TryGetValue(agentKey, out var pending)) return false;
        if (pending.Expires < DateTime.UtcNow) { _pending.TryRemove(agentKey, out _); return false; }
        if (pending.Pin != pin) return false;
        _pending.TryRemove(agentKey, out _);
        decision = pending.Decision;
        duration = pending.Duration;
        return true;
    }

    public string IssueToken(TimeSpan duration)
    {
        Span<byte> buf = stackalloc byte[16];
        RandomNumberGenerator.Fill(buf);
        var hex = Convert.ToHexString(buf).ToLowerInvariant();
        _tokens[hex] = DateTime.UtcNow + duration;
        return hex;
    }

    public bool IsTokenValid(string token)
    {
        if (!_tokens.TryGetValue(token, out var exp)) return false;
        if (exp < DateTime.UtcNow) { _tokens.TryRemove(token, out _); return false; }
        return true;
    }

    public bool TryGetApproval(string fileId, string agentKey, string pin, out DateTime until)
    {
        var k = Key(fileId, agentKey, pin);
        if (_approvedUntil.TryGetValue(k, out until) && until > DateTime.UtcNow)
            return true;
        until = DateTime.MinValue;
        return false;
    }

    public DateTime Approve(string fileId, string agentKey, string pin, TimeSpan duration)
    {
        var k = Key(fileId, agentKey, pin);
        var until = DateTime.UtcNow + duration;
        _approvedUntil[k] = until;
        return until;
    }

    public void Block(string fileId, string agentKey, string pin)
    {
        var k = Key(fileId, agentKey, pin);
        _approvedUntil.TryRemove(k, out _);
    }

    private static string Key(string fileId, string agentKey, string pin)
        => $"{fileId}|{agentKey}|{pin}";
}
