using System.Security.Cryptography;
using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>
/// A direct message is signed by the device that wrote it, like a room message, and says which account that device
/// belongs to (with the account's certificate for it). Anyone holding the message can check who wrote it and who it
/// was for, so a copy passed between your devices cannot be made up by one of them.
/// </summary>
internal static class DirectProof
{
    public readonly record struct Proof(string Pub, string Acct, string Cert, string Sig);

    public static Proof Create(PeerIdentity me, PeerId recipientAccount, string id, long ts, string text)
    {
        var sig = me.Sign(Payload(recipientAccount, me.Id, id, ts, text));
        bool linked = me.DeviceCertificate != null;
        return new Proof(Convert.ToBase64String(me.PublicKey),
            linked ? Convert.ToBase64String(me.AccountPublicKey) : "",
            linked ? Convert.ToBase64String(me.DeviceCertificate!) : "",
            Convert.ToBase64String(sig));
    }

    /// <summary>
    /// Whether the record carries a valid signature from a device of the right account: for a message you received
    /// that is the other person's account (<see cref="DirectRecord.Peer"/>), for one you sent it is your own.
    /// </summary>
    public static bool Verify(DirectRecord r, PeerId ownAccount)
    {
        if (r.Sig.Length == 0 || r.Pub.Length == 0) return false;
        try
        {
            var pub = Convert.FromBase64String(r.Pub);
            var acct = r.Acct.Length == 0 ? pub : Convert.FromBase64String(r.Acct);
            var device = PeerId.FromPublicKey(pub);
            var authorAccount = PeerId.FromPublicKey(acct);
            if (r.Acct.Length == 0 != (r.Cert.Length == 0)) return false;
            if (r.Acct.Length > 0 && !PeerIdentity.VerifyDevice(acct, pub, Convert.FromBase64String(r.Cert))) return false;

            if (authorAccount.Value != (r.Mine ? ownAccount.Value : r.Peer)) return false;
            var recipient = r.Mine ? new PeerId(r.Peer) : ownAccount;
            return PeerIdentity.Verify(pub, Payload(recipient, device, r.Id, r.St, r.Text), Convert.FromBase64String(r.Sig));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    private static byte[] Payload(PeerId recipient, PeerId device, string id, long ts, string text)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-dm-v1"); w.Write(recipient.Value); w.Write(device.Value);
            w.Write(id); w.Write(ts); w.Write(text);
        }
        return ms.ToArray();
    }
}
