using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yaparr.Identity;

namespace Yaparr.Chat;

// Direct messages through a mailbox (see docs/mailbox.md). A conversation has no room secret to lock a mailbox with, so the two people share a secret of their own:
// the first of them to need one makes it and hands it to the other over the connection between them (Wire.DmKey: the connection is already authenticated and
// encrypted, so the secret is theirs alone), and it is kept as one more setting of each account (dm:<the other account>), which is how each person's other devices get it,
// by the devices' own sync and through the account's cloud copy. If both make one at once, the secret of the account with the lower id is kept by both.
public sealed partial class ChatNode
{
    private const string DmPrefix = "dm:";
    private const int MaxDmSecrets = 2000;

    private readonly object _dmGate = new();

    /// <summary>A direct message as it is left in a mailbox: the signed record, so whoever reads it can check who wrote it and for whom.</summary>
    private sealed record StoredDirect(string I, long T, string X, string P, string A, string C, string S, string N);

    private byte[]? DmSecret(PeerId account)
    {
        lock (_acctGate)
        {
            if (!_acct.TryGetValue(DmPrefix + account.Value, out var e) || e.Deleted || e.Value == null) return null;
            try { return Convert.FromBase64String(e.Value) is { Length: 32 } secret ? secret : null; }
            catch (FormatException) { return null; }
        }
    }

    private List<(PeerId Account, byte[] Secret)> DmSecrets()
    {
        List<AccountEntry> entries;
        lock (_acctGate) entries = _acct.Values.Where(e => !e.Deleted && e.Value != null && e.Key.StartsWith(DmPrefix, StringComparison.Ordinal)).ToList();
        var list = new List<(PeerId, byte[])>();
        foreach (var e in entries)
        {
            var who = e.Key[DmPrefix.Length..];
            if (!IsValidPeerId(who)) continue;
            try { if (Convert.FromBase64String(e.Value!) is { Length: 32 } secret) list.Add((new PeerId(who), secret)); }
            catch (FormatException) { }
        }
        return list;
    }

    private byte[] EnsureDmSecret(PeerId account)
    {
        lock (_dmGate)
        {
            if (DmSecret(account) is { } known) return known;
            var made = RandomNumberGenerator.GetBytes(32);
            Stamp(DmPrefix + account.Value, Convert.ToBase64String(made), deleted: false);
            return made;
        }
    }

    // The id and key of the mailbox of the conversation between this account and another, from their shared secret. The same on both sides and on every device.
    private (string Id, byte[] Key) DmMailbox(byte[] secret, PeerId other)
    {
        var (low, high) = string.CompareOrdinal(AccountId.Value, other.Value) <= 0 ? (AccountId.Value, other.Value) : (other.Value, AccountId.Value);
        var info = "/" + low + "/" + high;
        var id = Base64Url.EncodeToString(HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt: null, info: Encoding.ASCII.GetBytes("yaparr/dm-mailbox-id/v1" + info)));
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt: null, info: Encoding.ASCII.GetBytes("yaparr/dm-mailbox-key/v1" + info));
        return (id, key);
    }

    private IEnumerable<string> DirectMailboxIds() => DmSecrets().Select(d => DmMailbox(d.Secret, d.Account).Id);

    private void SendDmKey(PeerState peer, byte[] secret) =>
        Send(peer.Connection, new Wire { Type = Wire.DmKey, Text = Convert.ToBase64String(secret) });

    // When a device of someone we already share a secret with connects, it is told the secret again (it may have been made before it was linked or restored).
    private void SendDmKeyIfKnown(PeerState peer)
    {
        if (!MailboxEnabled || IsSibling(peer)) return;
        if (DmSecret(peer.AccountId) is { } secret) SendDmKey(peer, secret);
    }

    private void OnDmKey(PeerState peer, string text)
    {
        if (IsSibling(peer) || peer.Name == null || IsBlocked(peer.Id)) return;
        byte[] secret;
        try { secret = Convert.FromBase64String(text); }
        catch (FormatException) { return; }
        if (secret.Length != 32) return;
        var account = peer.AccountId;
        lock (_dmGate)
        {
            if (DmSecret(account) is { } mine)
            {
                if (mine.AsSpan().SequenceEqual(secret)) return;
                // Two secrets for one conversation: the lower account id's is kept. Ours wins: the other side is told it, and takes it.
                if (string.CompareOrdinal(AccountId.Value, account.Value) < 0) { SendDmKey(peer, mine); return; }
            }
            else if (DmSecrets().Count >= MaxDmSecrets) return;
            Stamp(DmPrefix + account.Value, Convert.ToBase64String(secret), deleted: false);
        }
    }

    // A direct message just written: the other person is given the secret if they have not got it, and the signed message is sealed for the conversation's mailbox.
    private void QueueDirectForMailbox(IReadOnlyList<PeerState> devices, PeerId recipient, Wire wire, DirectProof.Proof proof)
    {
        if (!MailboxEnabled) return;
        var secret = EnsureDmSecret(recipient);
        foreach (var device in devices) SendDmKey(device, secret);
        if (_mailboxQueue.Count >= MaxMailboxQueue) return;
        var (id, key) = DmMailbox(secret, recipient);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new StoredDirect(wire.Id!, wire.Ts, wire.Text!, proof.Pub, proof.Acct, proof.Cert, proof.Sig, DisplayName));
        _mailboxQueue.Enqueue(new MailboxPost(id, wire.Id!, MailboxSeal.SealBytes(key, id, plain)));
        MailboxQueued?.Invoke();
    }

    private int ReceiveDirectFromMailbox(string mailboxId, IReadOnlyList<string> bodies)
    {
        foreach (var (account, secret) in DmSecrets())
        {
            var (id, key) = DmMailbox(secret, account);
            if (!string.Equals(id, mailboxId, StringComparison.Ordinal)) continue;
            int good = 0;
            foreach (var body in bodies)
            {
                if (!MailboxSeal.TryOpenBytes(key, id, body, out var plain)) continue;
                StoredDirect? item;
                try { item = JsonSerializer.Deserialize<StoredDirect>(plain); }
                catch (JsonException) { continue; }
                if (item != null && AcceptDirectItem(account, item)) good++;
            }
            return good;
        }
        return 0;
    }

    // Checks a direct message found in a mailbox exactly as one that arrives over a connection is checked (who wrote it, for whom, that the signature holds), keeps it, and
    // shows it. Returns whether it was good (also when it was already held).
    private bool AcceptDirectItem(PeerId peerAccount, StoredDirect item)
    {
        if (item.I is not { Length: > 0 and <= 64 } || item.X is not { Length: <= DirectTextMax } || item.P is not { Length: > 0 and <= 200 }
            || (item.A?.Length ?? 0) > 200 || (item.C?.Length ?? 0) > 400 || item.S is not { Length: > 0 and <= 200 }) return false;
        if (item.T > DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds()) return false;
        PeerId device, author;
        try
        {
            var pub = Convert.FromBase64String(item.P);
            device = PeerId.FromPublicKey(pub);
            author = PeerId.FromPublicKey(string.IsNullOrEmpty(item.A) ? pub : Convert.FromBase64String(item.A));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or CryptographicException) { return false; }

        bool mine = author == AccountId;
        if (!mine && author != peerAccount) return false;
        var name = CleanName(item.N ?? "") is { Length: > 0 } clean ? clean : peerAccount.Short;
        var record = new DirectRecord(item.I, peerAccount.Value, mine ? DirectNameOf(peerAccount) ?? peerAccount.Short : name, mine, item.T, item.X, item.P, item.A ?? "", item.C ?? "", item.S, item.T);
        if (!DirectProof.Verify(record, AccountId)) return false;
        if (!mine && IsBlocked(peerAccount)) return true;

        bool conversationExisted = HasDirectConversation(peerAccount);
        if (!RecordDirect(record, mirror: !mine)) return true;
        // What this account wrote on another device is in the history now; the chat shows it when it is next opened.
        if (mine) return true;

        bool fresh = item.T >= DateTimeOffset.UtcNow.Subtract(MailboxFreshWindow).ToUnixTimeMilliseconds();
        var info = GetPeerInfo(peerAccount) ?? new PeerInfo(device, name, null, false, peerAccount);
        var message = BuildMessage(info, null, item.I, item.X, DateTimeOffset.FromUnixTimeMilliseconds(item.T), catchUp: !fresh);
        bool existed = _directChats.ContainsKey(peerAccount);
        var chat = GetDirectChat(peerAccount);
        if (!existed) IncomingDirectChat?.Invoke(chat);
        chat.RaiseMessage(message);
        AutoFetch(message, trustedOnly: !conversationExisted);
        return true;
    }
}
