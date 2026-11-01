using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

// A direct message written while the other person cannot be reached is kept and sent when one of their devices connects, instead of
// being refused. It stays in the queue for as long as direct messages are kept (a week), also across a restart: only the ids are
// written down (pending.json); the message itself, with the proof of who wrote it, is the record in the direct history.
public sealed partial class ChatNode
{
    private sealed record PendingDirect(string Id, string Peer);

    private readonly object _pendingGate = new();
    private readonly Dictionary<string, PendingDirect> _pendingDirect = new();

    private string? PendingDirectPath => _options.DirectHistoryDirectory is { } dir ? Path.Combine(dir, "pending.json") : null;

    private long MaxQueuedAgeMs => (long)_options.MaxHistoryAge.TotalMilliseconds;

    // Is there a conversation with this person to add to? A first message has to wait for a connection, as before.
    private bool HasDirectConversation(PeerId account) => _directHistory.For(account.Value).Count > 0;

    private void QueueDirect(Outgoing outgoing, PeerId recipient)
    {
        outgoing.Queued = true;
        lock (_pendingGate) _pendingDirect[outgoing.Id] = new PendingDirect(outgoing.Id, recipient.Value);
        SavePendingDirect();
    }

    private void ForgetPendingDirect(string id)
    {
        lock (_pendingGate) if (!_pendingDirect.Remove(id)) return;
        SavePendingDirect();
    }

    private void SavePendingDirect()
    {
        if (PendingDirectPath is not { } path) return;
        try
        {
            PendingDirect[] all;
            lock (_pendingGate) all = _pendingDirect.Values.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(all, Json));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not save the queue of direct messages: " + ex.Message);
        }
    }

    // After a restart: the messages that were still waiting are put back in the outbox, rebuilt from the history.
    private void LoadPendingDirect()
    {
        if (PendingDirectPath is not { } path || !File.Exists(path)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<PendingDirect[]>(File.ReadAllBytes(path), Json) ?? Array.Empty<PendingDirect>();
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var p in stored)
            {
                var record = _directHistory.For(p.Peer).FirstOrDefault(r => r.Id == p.Id && r.Mine && r.Sig.Length > 0);
                if (record == null || now - record.Ts > MaxQueuedAgeMs || !IsValidPeerId(p.Peer)) continue;
                var wire = new Wire { Type = Wire.Message, Id = record.Id, Text = record.Text, Ts = record.St > 0 ? record.St : record.Ts, Pub = record.Pub, Acct = record.Acct.Length > 0 ? record.Acct : null, Cert = record.Cert.Length > 0 ? record.Cert : null, Sig = record.Sig };
                var outgoing = new Outgoing(record.Id)
                {
                    DirectPeer = new PeerId(p.Peer),
                    DirectWire = wire,
                    // How long it has waited, as a tick count, so that it expires on time.
                    SentAt = Environment.TickCount64 - (now - record.Ts),
                    Queued = true,
                };
                _outbox[record.Id] = outgoing;
                lock (_pendingGate) _pendingDirect[record.Id] = p;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("could not read the queue of direct messages: " + ex.Message);
        }
    }
}
