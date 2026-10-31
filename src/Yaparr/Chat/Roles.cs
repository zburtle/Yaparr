using System.Security.Cryptography;
using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>What someone may do in a room. Admins are appointed by the owner; the owner is named in the invite and never changes.</summary>
public enum RoomRole { Member, Admin, Owner }

/// <summary>
/// The owner's signed statement that an account is, or is no longer, an admin of a room. It is signed by one of the owner's
/// devices (with the account's certificate for it, as a direct message is), so it can be checked by anyone who has the
/// invite, which names the owner. The newest statement about an account wins. Every device of the owner holds the account
/// key, so a lost or stolen one can sign these for good; there is no revoking it and the owner cannot be changed.
/// </summary>
public sealed record StoredRole(string Pub, string Acct, string Cert, string Subject, bool Admin, long Ts, string Sig);

internal static class SignedRole
{
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    public static StoredRole Create(PeerIdentity identity, string roomKey, PeerId subject, bool admin, long timestamp)
    {
        bool linked = identity.DeviceCertificate != null;
        var unsigned = new StoredRole(Convert.ToBase64String(identity.PublicKey),
            linked ? Convert.ToBase64String(identity.AccountPublicKey) : "",
            linked ? Convert.ToBase64String(identity.DeviceCertificate!) : "",
            subject.Value, admin, timestamp, "");
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, identity.Id, unsigned))) };
    }

    /// <summary>Whether the statement was signed by a device of <paramref name="owner"/>.</summary>
    public static bool Verify(StoredRole r, string roomKey, PeerId owner)
    {
        if (r.Subject is null || r.Pub is null || r.Acct is null || r.Cert is null || r.Sig is null) return false;
        if (r.Subject.Length != 64 || !r.Subject.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        if (r.Ts > DateTimeOffset.UtcNow.Add(MaxFutureSkew).ToUnixTimeMilliseconds()) return false;
        try
        {
            var pub = Convert.FromBase64String(r.Pub);
            var acct = r.Acct.Length == 0 ? pub : Convert.FromBase64String(r.Acct);
            if (r.Acct.Length == 0 != (r.Cert.Length == 0)) return false;
            if (r.Acct.Length > 0 && !PeerIdentity.VerifyDevice(acct, pub, Convert.FromBase64String(r.Cert))) return false;
            if (PeerId.FromPublicKey(acct) != owner) return false;
            return PeerIdentity.Verify(pub, Payload(roomKey, PeerId.FromPublicKey(pub), r), Convert.FromBase64String(r.Sig));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    private static byte[] Payload(string roomKey, PeerId device, StoredRole r)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-role-v1"); w.Write(roomKey); w.Write(device.Value); w.Write(r.Subject); w.Write(r.Admin); w.Write(r.Ts);
        }
        return ms.ToArray();
    }
}
