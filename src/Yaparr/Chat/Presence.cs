using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

public sealed record StoredPresence(string From, string Pub, string Name, long Ts, bool Gone, string Sig);

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
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, unsigned))) };
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
