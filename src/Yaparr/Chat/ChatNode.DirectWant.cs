using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Chat;

// Asking for particular messages of a direct conversation that this device lacks, such as the one a reply answers (a room has the same with a `want`
// message). Scrolling back asks for a page of earlier messages (see DirectBackfill); this asks for the messages themselves, by the keys replies name them by.
public sealed partial class ChatNode
{
    // Reading a conversation to find a key stops here: a conversation is not that long, and a key that is not found is not worth reading for ever.
    private const int DirectKeyScanMax = 50_000;

    /// <summary>The keys a message of a direct conversation is named by, which differ with who wrote it and from which device it is seen (see <see cref="SendDirectRecords"/>).</summary>
    private IEnumerable<string> MessageKeysOf(DirectRecord r, PeerId person)
    {
        yield return ChatMessage.KeyOf(Id, r.Id);
        yield return ChatMessage.KeyOf(AccountId, r.Id);
        yield return ChatMessage.KeyOf(person, r.Id);
        if (r.Pub.Length == 0) yield break;
        string? pubKey = null;
        try { pubKey = ChatMessage.KeyOf(PeerId.FromPublicKey(Convert.FromBase64String(r.Pub)), r.Id); }
        catch (FormatException) { }
        if (pubKey != null) yield return pubKey;
    }

    // The messages of a conversation held here that go by one of these keys (reactions are not messages and are left out).
    private List<DirectRecord> FindDirectByKeys(string key, HashSet<string> wanted)
    {
        var found = new List<DirectRecord>();
        var person = new PeerId(key);
        long ts = long.MaxValue - 1;
        string cursor = "";
        for (int read = 0; read < DirectKeyScanMax; )
        {
            var page = _directHistory.Before(key, ts, cursor, 500);
            if (page.Count == 0) break;
            foreach (var r in page)
                if (!MessageEnvelope.IsReactionText(r.Text) && MessageKeysOf(r, person).Any(wanted.Contains)) found.Add(r);
            read += page.Count;
            var last = page[^1];
            ts = last.Ts;
            cursor = DirectHistory.Key(last);
            if (found.Count >= wanted.Count) break;
        }
        return found;
    }

    /// <summary>
    /// Brings messages of a conversation to the screen by the keys replies name them by: those held here but not shown (older than what was read) are shown, and the
    /// rest are asked for from your other devices and the person's. Returns how many devices were asked, or -1 when it was all held here.
    /// </summary>
    internal int RequestDirectMessages(PeerId person, IEnumerable<string> keys)
    {
        var wanted = keys.Where(MessageEnvelope.IsKey).Select(k => k.ToLowerInvariant()).Distinct().Take(MaxWanted).ToHashSet();
        if (wanted.Count == 0) return 0;
        var held = FindDirectByKeys(person.Value, wanted);
        foreach (var r in held)
            if (IsShownDirect(r, person)) ShowDirect(r, catchUp: true);
        var stillWanted = wanted.Where(k => !held.Any(r => MessageKeysOf(r, person).Contains(k))).ToArray();
        if (stillWanted.Length == 0) return held.Count > 0 ? -1 : 0;
        if (!_options.SyncHistory) return 0;

        // Our own devices first (they hold the conversation as we do), then the person's.
        var asked = _peers.Values.Where(p => p.Connection.State == ConnectionState.Established && !IsBlocked(p.Id)
                                              && (IsSibling(p) || p.AccountId == person && p.Id != Id))
            .OrderBy(p => IsSibling(p) ? 0 : 1).ThenBy(p => p.Connection.IsRelayed).Take(4).ToList();
        long now = Environment.TickCount64;
        foreach (var peer in asked)
        {
            _directBackAsked[peer.Id] = (person.Value, now + DirectBackWindowMs, true);
            Send(peer.Connection, new Wire { Type = Wire.DirectBack, Text = person.Value, Have = stillWanted });
        }
        return asked.Count;
    }

    private void OnDirectWantRequest(PeerState peer, string partner, string[] keys)
    {
        if (!_options.SyncHistory || IsBlocked(peer.Id) || !peer.TryStartWanting("dmwant")) return;
        bool sibling = IsSibling(peer);
        // The other person's device is told about the conversation with it, whatever it names; our own device may ask about any conversation.
        string key = sibling ? partner : peer.AccountId.Value;
        if (!IsValidPeerId(key)) return;
        if (!sibling && IsBlocked(peer.AccountId)) return;
        var wanted = keys.Take(MaxWanted).Where(MessageEnvelope.IsKey).Select(k => k.ToLowerInvariant()).ToHashSet();
        if (wanted.Count == 0) return;
        var records = FindDirectByKeys(key, wanted).Where(r => r.Sig.Length > 0).ToList();
        if (records.Count == 0) return;
        SendDirectRecords(peer, key, records, sibling);
    }
}
