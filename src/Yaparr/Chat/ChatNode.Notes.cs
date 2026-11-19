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
}
