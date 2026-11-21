using Yaparr.Identity;

namespace Yaparr.Chat;

// "Notes to self": a direct chat with your own account that every account has, for quick notes shared between your own devices. It is the direct chat of
// <see cref="AccountId"/>, so it is kept, passed between your devices (ChatNode.DirectSync) and left in the account's mailbox for devices that are away
// (ChatNode.DirectMailbox) like any other direct chat, and the proofs of who wrote a note are the usual ones. What differs: a note is never sent to anyone as
// a message (your other devices get the signed record, the same copy they swap for every direct chat), it has no connection to wait for, it is never unread and never
// notifies (everything in it is yours), and the person is not someone else: the chat has no peer and is not in the list of people.
public sealed partial class ChatNode
{
    /// <summary>What the chat with yourself is called.</summary>
    public const string NotesName = "Notes to self";

    /// <summary>The chat with yourself. It always exists, also on an account that never wrote a note, and cannot be removed.</summary>
    public DirectChat NotesChat => GetDirectChat(AccountId);

    /// <summary>Whether this direct chat is the one with yourself (a device of your own counts, so does your account).</summary>
    public bool IsNotesChat(PeerId person) => person == AccountId || person == Id || AccountOf(person) == AccountId;

    // A note: signed like a direct message, for your own account. Not sent as a message to your devices; they receive the record.
    private string SendNote(string text)
    {
        var wire = NewMessage(text, room: null);
        var proof = DirectProof.Create(_identity, AccountId, wire.Id!, wire.Ts, text);
        // A device of yours that is away reads it from the account's mailbox (the secret of the account's own conversation is one more setting, so it syncs).
        QueueDirectForMailbox(Array.Empty<PeerState>(), AccountId, wire, proof);
        RecordDirect(new DirectRecord(wire.Id!, AccountId.Value, DisplayName, true, wire.Ts, text, proof.Pub, proof.Acct, proof.Cert, proof.Sig, wire.Ts), mirror: true);
        return wire.Id!;
    }

    /// <summary>
    /// Deletes a note (by its <see cref="ChatMessage.Id"/>) for good, on every device of the account: it is removed here, remembered as removed (so no copy that turns up later
    /// from another device, the mailbox or the kept history brings it back), and your connected devices are told. False when there is no such note.
    /// </summary>
    internal bool DeleteNote(string messageId)
    {
        var key = DirectHistory.KeyFor(AccountId.Value, messageId, mine: true);
        if (_directHistory.Find(AccountId.Value, key) == null) return false;
        _directHistory.RemoveForGood(AccountId.Value, new[] { key });
        SendNotesRemoved(_peers.Values.Where(IsSibling), new[] { key });
        return true;
    }

    /// <summary>Which of these notes (by id) were deleted.</summary>
    internal HashSet<string> RemovedNotesAmong(IEnumerable<string> messageIds)
    {
        var removed = _directHistory.RemovedKeys(AccountId.Value);
        if (removed.Count == 0) return new HashSet<string>();
        var keys = new HashSet<string>(removed, StringComparer.Ordinal);
        return messageIds.Where(id => keys.Contains(DirectHistory.KeyFor(AccountId.Value, id, mine: true))).ToHashSet();
    }

    private void SendNotesRemoved(IEnumerable<PeerState> to, IReadOnlyList<string> keys)
    {
        foreach (var sibling in to.ToList())
            for (int i = 0; i < keys.Count; i += DirectSyncKeys)
                Send(sibling.Connection, new Wire { Type = Wire.NotesRemoved, Have = keys.Skip(i).Take(DirectSyncKeys).ToArray() });
    }

    // Only a device of your own account may say a note is gone.
    private void OnNotesRemoved(PeerState peer, string[] keys)
    {
        if (!IsSibling(peer)) return;
        var good = keys.Take(DirectSyncKeys).Where(k => k.Length == 16 && k.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))).Distinct().ToList();
        if (good.Count > 0 && _directHistory.RemoveForGood(AccountId.Value, good) > 0) NotesChat.RaiseHistoryChanged();
    }
}
