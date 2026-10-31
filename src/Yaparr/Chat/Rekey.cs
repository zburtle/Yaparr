using System.Security.Cryptography;
using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>
/// The owner's signed order to replace a room's secret, carrying the new secret and the accounts who are to have it. It travels
/// only over the encrypted connections between people on the list: a member who gets it checks the owner's signature, that
/// their own account is listed, and then passes it on to the listed people they are connected to and nobody else. Someone who
/// is not listed (a banned person, or a new identity made to sneak in) never sees the new secret.
/// </summary>
/// <param name="Secret">The new secret, base64.</param>
/// <param name="Epoch">How many times the secret has been replaced, this time included. A package with a lower number than the room's is ignored.</param>
/// <param name="To">Account ids of the people who are given the new secret, apart from the owner.</param>
public sealed record StoredRekey(string Pub, string Acct, string Cert, string Secret, int Epoch, string[] To, long Ts, string Sig);

internal static class SignedRekey
{
    public const int MaxRecipients = 256;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    public static StoredRekey Create(PeerIdentity identity, string roomId, byte[] secret, int epoch, IEnumerable<string> to, long timestamp)
    {
        bool linked = identity.DeviceCertificate != null;
        var unsigned = new StoredRekey(Convert.ToBase64String(identity.PublicKey),
            linked ? Convert.ToBase64String(identity.AccountPublicKey) : "",
            linked ? Convert.ToBase64String(identity.DeviceCertificate!) : "",
            Convert.ToBase64String(secret), epoch, to.OrderBy(a => a, StringComparer.Ordinal).ToArray(), timestamp, "");
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomId, identity.Id, unsigned))) };
    }

    /// <summary>Whether the package is well formed and was signed by a device of <paramref name="owner"/>.</summary>
    public static bool Verify(StoredRekey r, string roomId, PeerId owner)
    {
        if (r.Pub is null || r.Acct is null || r.Cert is null || r.Secret is null || r.To is null || r.Sig is null) return false;
        if (r.Epoch is < 1 or > 65535 || r.To.Length > MaxRecipients) return false;
        if (r.To.Any(a => a is null || a.Length != 64 || !a.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))) return false;
        if (r.Ts > DateTimeOffset.UtcNow.Add(MaxFutureSkew).ToUnixTimeMilliseconds()) return false;
        try
        {
            if (Convert.FromBase64String(r.Secret).Length != 32) return false;
            var pub = Convert.FromBase64String(r.Pub);
            var acct = r.Acct.Length == 0 ? pub : Convert.FromBase64String(r.Acct);
            if (r.Acct.Length == 0 != (r.Cert.Length == 0)) return false;
            if (r.Acct.Length > 0 && !PeerIdentity.VerifyDevice(acct, pub, Convert.FromBase64String(r.Cert))) return false;
            if (PeerId.FromPublicKey(acct) != owner) return false;
            return PeerIdentity.Verify(pub, Payload(roomId, PeerId.FromPublicKey(pub), r), Convert.FromBase64String(r.Sig));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    /// <summary>A fixed order between two packages for the same epoch, so owners' devices that rotated at once end up on the same one.</summary>
    public static string Hash(StoredRekey r) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.Sig ?? ""))).ToLowerInvariant();

    private static byte[] Payload(string roomId, PeerId device, StoredRekey r)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-rekey-v1"); w.Write(roomId); w.Write(device.Value); w.Write(r.Secret); w.Write(r.Epoch);
            w.Write(string.Join(",", r.To)); w.Write(r.Ts);
        }
        return ms.ToArray();
    }
}
