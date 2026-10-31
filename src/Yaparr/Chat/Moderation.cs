using System.Security.Cryptography;
using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>Someone the room's admins have banned.</summary>
public sealed record BannedPerson(PeerId Account, string Name);

/// <summary>
/// An owner's or admin's signed order about the room: ban or unban an account, delete a message, or delete a sound from the
/// soundboard. Whether the signer was allowed to give it is decided by every client from the current list of admins, so an
/// admin who is removed takes their orders with them, and an admin cannot ban another admin or the owner.
/// </summary>
/// <param name="Kind">"ban", "unban", "delmsg" or "delsound".</param>
/// <param name="Target">An account id (ban, unban), a message key (delmsg) or a sound id (delsound).</param>
/// <param name="Label">The banned person's name as the admin saw it, so a list of bans can say who they are.</param>
public sealed record StoredMod(string Pub, string Acct, string Cert, string Kind, string Target, string Label, long Ts, string Sig);

internal static class SignedMod
{
    public const string Ban = "ban", Unban = "unban", DeleteMessage = "delmsg", DeleteSound = "delsound";
    private const int MaxLabel = 64;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);

    public static StoredMod Create(PeerIdentity identity, string roomKey, string kind, string target, string label, long timestamp)
    {
        bool linked = identity.DeviceCertificate != null;
        var unsigned = new StoredMod(Convert.ToBase64String(identity.PublicKey),
            linked ? Convert.ToBase64String(identity.AccountPublicKey) : "",
            linked ? Convert.ToBase64String(identity.DeviceCertificate!) : "",
            kind, target, label.Length <= MaxLabel ? label : label[..MaxLabel], timestamp, "");
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, identity.Id, unsigned))) };
    }

    /// <summary>Whether the statement is well formed and really signed by a device of <paramref name="signer"/>. Not whether that account may give the order.</summary>
    public static bool Verify(StoredMod m, string roomKey, out PeerId signer)
    {
        signer = default;
        if (m.Pub is null || m.Acct is null || m.Cert is null || m.Kind is null || m.Target is null || m.Label is null || m.Sig is null) return false;
        if (m.Label.Length > MaxLabel || !ValidTarget(m.Kind, m.Target)) return false;
        if (m.Ts > DateTimeOffset.UtcNow.Add(MaxFutureSkew).ToUnixTimeMilliseconds()) return false;
        try
        {
            var pub = Convert.FromBase64String(m.Pub);
            var acct = m.Acct.Length == 0 ? pub : Convert.FromBase64String(m.Acct);
            if (m.Acct.Length == 0 != (m.Cert.Length == 0)) return false;
            if (m.Acct.Length > 0 && !PeerIdentity.VerifyDevice(acct, pub, Convert.FromBase64String(m.Cert))) return false;
            if (!PeerIdentity.Verify(pub, Payload(roomKey, PeerId.FromPublicKey(pub), m), Convert.FromBase64String(m.Sig))) return false;
            signer = PeerId.FromPublicKey(acct);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException) { return false; }
    }

    private static bool ValidTarget(string kind, string target) => kind switch
    {
        Ban or Unban => IsHex(target, 64),
        DeleteMessage => IsHex(target, 32),
        DeleteSound => IsHex(target, 16),
        _ => false,
    };

    private static bool IsHex(string s, int length) => s.Length == length && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>One slot per signer and subject: a signer's newer word on the same thing replaces their older one.</summary>
    public static string SlotOf(StoredMod m, PeerId signer) => (m.Kind is Ban or Unban ? Ban : m.Kind) + ":" + m.Target + ":" + signer.Value;

    private static byte[] Payload(string roomKey, PeerId device, StoredMod m)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-mod-v1"); w.Write(roomKey); w.Write(device.Value); w.Write(m.Kind); w.Write(m.Target); w.Write(m.Label); w.Write(m.Ts);
        }
        return ms.ToArray();
    }
}

/// <summary>What a room's orders add up to right now.</summary>
internal sealed class ModView
{
    public readonly Dictionary<string, string> Banned = new();
    public readonly HashSet<string> Messages = new();
    public readonly HashSet<string> Sounds = new();
}
