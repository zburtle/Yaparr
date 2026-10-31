using Yaparr.Identity;

namespace Yaparr.Chat;

// A direct message goes to every device of the person it is for. So that your own devices show the same
// conversations, each one also passes a copy of every direct message it sends or receives to your other devices,
// and two of your devices that meet swap the ones either is missing. Only a device with a certificate from your own
// account may do this, the same rule as for your other settings.
public sealed partial class ChatNode
{
    private const int DirectBatch = 40, DirectSyncKeys = 2000, DirectCatchUpMax = 1000, DirectPerFrame = 100;

    private readonly DirectHistory _directHistory;

    // Keeps a direct message and passes it to the other devices of this account. False if it was already known.
    private bool RecordDirect(PeerId peer, string name, bool mine, string id, long ts, string text, bool mirror)
    {
        var record = new DirectRecord(id, peer.Value, CleanName(name), mine, ts, text);
        if (!DirectHistory.IsValid(record) || !_directHistory.TryAdd(record)) return false;
        if (mirror) SendDirectRecords(_peers.Values.Where(IsSibling), new[] { record }, catchUp: false);
        return true;
    }

    private void SendDirectRecords(IEnumerable<PeerState> to, IReadOnlyList<DirectRecord> records, bool catchUp)
    {
        foreach (var sibling in to.ToList())
            for (int i = 0; i < records.Count; i += DirectBatch)
                Send(sibling.Connection, new Wire { Type = Wire.Direct, Dm = records.Skip(i).Take(DirectBatch).ToArray(), Cu = catchUp ? true : null });
    }

    // A device that has just connected says which messages it has; the other side sends what is missing.
    private void SendDirectSync(PeerState sibling) =>
        Send(sibling.Connection, new Wire { Type = Wire.DirectSync, Have = _directHistory.Keys(DirectSyncKeys) });

    private void OnDirectSync(PeerState peer, string[] have)
    {
        if (!IsSibling(peer)) return;
        var missing = _directHistory.Missing(new HashSet<string>(have.Take(DirectSyncKeys)), DirectCatchUpMax);
        if (missing.Count > 0) SendDirectRecords(new[] { peer }, missing, catchUp: true);
    }

    private void OnDirectRecords(PeerState peer, DirectRecord[] records, bool catchUp)
    {
        if (!IsSibling(peer)) return;
        foreach (var r in records.Take(DirectPerFrame))
        {
            if (!DirectHistory.IsValid(r)) continue;
            // Someone you have blocked on any device stays blocked on all of them.
            if (!r.Mine && IsBlocked(new PeerId(r.Peer))) continue;
            long ts = Math.Min(r.Ts, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var record = r with { Name = CleanName(r.Name), Ts = ts };
            if (_directHistory.TryAdd(record)) ShowDirect(record, catchUp && !record.Mine);
        }
    }

    // A message that came from one of your other devices appears in its conversation like any other.
    private void ShowDirect(DirectRecord r, bool catchUp)
    {
        var person = new PeerId(r.Peer);
        var from = r.Mine
            ? new PeerInfo(Id, DisplayName, null, false, AccountId)
            : new PeerInfo(person, r.Name.Length > 0 ? r.Name : person.Short, null, false, person);
        var msg = BuildMessage(from, null, r.Id, r.Text, DateTimeOffset.FromUnixTimeMilliseconds(r.Ts), catchUp);
        bool existed = _directChats.ContainsKey(person);
        var chat = GetDirectChat(person);
        if (!existed) IncomingDirectChat?.Invoke(chat);
        chat.RaiseMessage(msg);
        if (r.Mine || IsAcquaintanceAccount(person)) AutoFetch(msg);
        else if (msg.Attachment is { } waiting && !_blobs.Has(waiting.Hash)) RaiseAttachment(waiting.Hash, AttachmentState.NeedsConfirmation, 0, waiting.Size);
    }

    private bool IsAcquaintanceAccount(PeerId account) =>
        _pinned.ContainsKey(account.Value) || DevicesOf(account).Any(IsAcquaintance)
        || _rooms.Values.Any(r => r.MemberStates.Keys.Any(k => AccountOf(k) == account));

    /// <summary>The conversations that have messages kept, so an app can show them again after a restart.</summary>
    public IReadOnlyList<DirectChat> StoredDirectChats =>
        _directHistory.Peers().Select(p => GetDirectChat(new PeerId(p))).ToList();

    internal IReadOnlyList<ChatMessage> GetDirectHistory(PeerId person) =>
        _directHistory.For(person.Value)
            .Where(r => r.Mine || !IsBlocked(person))
            .Select(r => BuildMessage(
                r.Mine ? new PeerInfo(Id, DisplayName, null, false, AccountId) : new PeerInfo(person, r.Name.Length > 0 ? r.Name : person.Short, null, false, person),
                null, r.Id, r.Text, DateTimeOffset.FromUnixTimeMilliseconds(r.Ts)))
            .ToList();

    internal string? DirectNameOf(PeerId person) => _directHistory.LastName(person.Value) is { Length: > 0 } name ? name : null;

    // What a conversation has been read up to is kept as one more setting of the account, so every device agrees.
    internal DateTimeOffset? DirectReadUpTo(PeerId person)
    {
        lock (_acctGate)
            return _acct.TryGetValue(ReadPrefix + person.Value, out var e) && !e.Deleted && long.TryParse(e.Value, out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    }

    internal void MarkDirectRead(PeerId person)
    {
        var newest = _directHistory.For(person.Value).Where(r => !r.Mine).Select(r => r.Ts).DefaultIfEmpty(0).Max();
        if (newest == 0 || DirectReadUpTo(person) is { } already && already.ToUnixTimeMilliseconds() >= newest) return;
        Stamp(ReadPrefix + person.Value, newest.ToString(), deleted: false);
    }
}
