using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <param name="Acct">For a device linked to an account: the account's public key. Not part of the signature, since <paramref name="Cert"/> proves it by itself.</param>
/// <param name="Cert">The account's signature over this device's key, which shows the device belongs to the account.</param>
public sealed record StoredPresence(string From, string Pub, string Name, long Ts, bool Gone, string Sig, string? Acct = null, string? Cert = null);

internal sealed record PresenceEntry(StoredPresence Beacon, long SeenAt);

internal static class SignedPresence
{
    private const int MaxName = 64;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    public static StoredPresence Create(PeerIdentity identity, string roomKey, string name, long timestamp, bool gone)
    {
        var unsigned = new StoredPresence(
            identity.Id.Value, Convert.ToBase64String(identity.PublicKey),
            name.Length <= MaxName ? name : name[..MaxName], timestamp, gone, "");
        var signed = unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, unsigned))) };
        // A linked device says whose it is, so that a block of the person reaches it even before we have ever connected to it.
        return identity.DeviceCertificate == null ? signed
            : signed with { Acct = Convert.ToBase64String(identity.AccountPublicKey), Cert = Convert.ToBase64String(identity.DeviceCertificate) };
    }

    /// <summary>The account a device belongs to, when the beacon carries a certificate that proves it.</summary>
    public static bool TryGetAccount(StoredPresence p, out PeerId device, out PeerId account)
    {
        device = default; account = default;
        if (string.IsNullOrEmpty(p.Acct) || string.IsNullOrEmpty(p.Cert) || p.Acct.Length > 200 || p.Cert.Length > 400) return false;
        try
        {
            var pub = Convert.FromBase64String(p.Pub);
            var accountKey = Convert.FromBase64String(p.Acct);
            if (!PeerIdentity.VerifyDevice(accountKey, pub, Convert.FromBase64String(p.Cert))) return false;
            device = PeerId.FromPublicKey(pub);
            account = PeerId.FromPublicKey(accountKey);
            return device.Value == p.From && device != account;
        }
        catch (FormatException) { return false; }
    }

    public static bool Verify(StoredPresence p, string roomKey)
    {
        if (p.Name.Length > MaxName) return false;
        var now = DateTimeOffset.UtcNow;
        if (p.Ts > now.Add(MaxFutureSkew).ToUnixTimeMilliseconds() || p.Ts < now.Subtract(MaxAge).ToUnixTimeMilliseconds()) return false;
        try
        {
            var pub = Convert.FromBase64String(p.Pub);
            if (PeerId.FromPublicKey(pub).Value != p.From) return false;
            return PeerIdentity.Verify(pub, Payload(roomKey, p), Convert.FromBase64String(p.Sig));
        }
        catch (FormatException) { return false; }
    }

    private static byte[] Payload(string roomKey, StoredPresence p)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-presence-v1"); w.Write(roomKey); w.Write(p.From); w.Write(p.Name); w.Write(p.Ts); w.Write(p.Gone);
        }
        return ms.ToArray();
    }
}
