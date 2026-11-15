using System.Collections.Concurrent;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Chat;

// A direct conversation is held by both people (and by each of their devices). A device that lacks the older part of it, because it is new or was restored
// from a backup, asks the other devices that are connected for it when someone scrolls back past the oldest message it holds: your own other devices, and the
// devices of the person you are talking with. What they send is checked as every direct message is checked: each carries the signature of the device that wrote it, with
// the account's certificate and the recipient inside the signature, so the ones who answer cannot make a message up or change one, only leave some out.
public sealed partial class ChatNode
{
    // Messages of each side are stored with the time that side saw them, which differs by a few seconds (and by clock error): asking from a little later than the
    // message at the edge leaves no gap, and what is already held is recognised and dropped.
    private const long DirectBackMarginMs = 30_000;

    private const long DirectBackWindowMs = 15_000;

    // Whom we asked and about whom: only answers from those, about that conversation, in that time are taken.
    private readonly ConcurrentDictionary<PeerId, (string Partner, long Until)> _directBackAsked = new();

    internal int RequestEarlierDirectHistory(PeerId person, string beforeId)
    {
        if (!_options.SyncHistory || _directHistory.FindById(person.Value, beforeId) is not { } at) return 0;
        // Our own devices first (they hold the conversation as we do), then the person's.
        var asked = _peers.Values.Where(p => p.Connection.State == ConnectionState.Established && !IsBlocked(p.Id)
                                              && (IsSibling(p) || p.AccountId == person && p.Id != Id))
            .OrderBy(p => IsSibling(p) ? 0 : 1).ThenBy(p => p.Connection.IsRelayed).Take(4).ToList();
        if (asked.Count == 0) return 0;
        long now = Environment.TickCount64;
        foreach (var peer in asked)
        {
            _directBackAsked[peer.Id] = (person.Value, now + DirectBackWindowMs);
            Send(peer.Connection, new Wire { Type = Wire.DirectBack, Text = person.Value, Ts = at.Ts + DirectBackMarginMs, N = BackfillMax });
        }
        return asked.Count;
    }

    private void OnDirectBack(PeerState peer, Wire wire)
    {
        if (wire.Dm != null) OnDirectBackData(peer, wire.Dm);
        else if (wire.Text != null) OnDirectBackRequest(peer, wire.Text, wire.Ts, wire.N);
    }

    private void OnDirectBackRequest(PeerState peer, string partner, long ts, int count)
    {
        if (!_options.SyncHistory || IsBlocked(peer.Id) || !peer.TryStartWanting("dmback")) return;
        bool sibling = IsSibling(peer);
        // The other person's device is told about the conversation with it, whatever it names; our own device may ask about any conversation.
        string key = sibling ? partner : peer.AccountId.Value;
        if (!IsValidPeerId(key)) return;
        if (!sibling && IsBlocked(peer.AccountId)) return;
        var records = _directHistory.Before(key, ts + 1, "", Math.Clamp(count, 1, BackfillMax)).Where(r => r.Sig.Length > 0).ToList();
        if (records.Count == 0) return;
        // The reactions on those messages go with them. A reaction names the message by the key its writer knew it by, which differs from one device to another
        // (a message of your own is keyed by the device you are on, one of someone else's by their account), so each message is looked for under every key it can have.
        var targets = new HashSet<string>();
        var person = new PeerId(key);
        foreach (var r in records)
        {
            targets.Add(ChatMessage.KeyOf(Id, r.Id));
            targets.Add(ChatMessage.KeyOf(AccountId, r.Id));
            targets.Add(ChatMessage.KeyOf(person, r.Id));
            try { if (r.Pub.Length > 0) targets.Add(ChatMessage.KeyOf(PeerId.FromPublicKey(Convert.FromBase64String(r.Pub)), r.Id)); }
            catch (FormatException) { }
        }
        records.AddRange(_directHistory.ReactionsTo(key, targets).Where(r => r.Sig.Length > 0));
        records = records.OrderByDescending(r => r.Ts).ToList();
        // To the other person the conversation is the other way round: what we sent they received, and it is with us.
        if (!sibling) records = records.Select(r => r with { Mine = !r.Mine, Peer = AccountId.Value }).ToList();
        records.Reverse();
        for (int i = 0; i < records.Count; i += DirectBatch)
            Send(peer.Connection, new Wire { Type = Wire.DirectBack, Dm = records.Skip(i).Take(DirectBatch).ToArray() });
    }

    private void OnDirectBackData(PeerState peer, DirectRecord[] records)
    {
        if (!_directBackAsked.TryGetValue(peer.Id, out var asked) || Environment.TickCount64 > asked.Until) return;
        var person = new PeerId(asked.Partner);
        int added = 0;
        foreach (var received in records.Take(DirectPerFrame))
        {
            // It is a message of the conversation we asked about, whatever the record says it is with.
            var r = received with { Peer = person.Value, Name = CleanName(received.Name) };
            if (!DirectHistory.IsValid(r) || !DirectProof.Verify(r, AccountId)) continue;
            if (!r.Mine && IsBlocked(person)) continue;
            r = r with { Ts = Math.Min(r.Ts, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) };
            if (!_directHistory.TryAdd(r)) continue;
            added++;
            if (!MessageEnvelope.IsReactionText(r.Text)) AutoFetch(ToDirectMessage(r, person), trustedOnly: !r.Mine && !IsAcquaintanceAccount(person));
        }
        if (added > 0) GetDirectChat(person).RaiseEarlierHistory();
    }
}
