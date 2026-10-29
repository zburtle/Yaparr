using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>A signed statement that someone is in (or has left) a room's voice channel.</summary>
public sealed record StoredVoice(string From, string Pub, string Name, long Ts, bool Active, bool Muted, bool Deafened, string Sig);

internal sealed record VoiceEntry(StoredVoice State, long SeenAt);

/// <summary>Who is in a room's voice channel and whether they have muted themselves.</summary>
public sealed record VoiceParticipant(PeerInfo Peer, bool Muted, bool Deafened, bool IsSelf);

internal static class SignedVoice
{
    private const int MaxName = 64;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    public static StoredVoice Create(PeerIdentity identity, string roomKey, string name, long timestamp, bool active, bool muted, bool deafened)
    {
        var unsigned = new StoredVoice(
            identity.Id.Value, Convert.ToBase64String(identity.PublicKey),
            name.Length <= MaxName ? name : name[..MaxName], timestamp, active, muted, deafened, "");
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, unsigned))) };
    }

    public static bool Verify(StoredVoice v, string roomKey)
    {
        if (v.Name.Length > MaxName) return false;
        var now = DateTimeOffset.UtcNow;
        if (v.Ts > now.Add(MaxFutureSkew).ToUnixTimeMilliseconds() || v.Ts < now.Subtract(MaxAge).ToUnixTimeMilliseconds()) return false;
        try
        {
            var pub = Convert.FromBase64String(v.Pub);
            if (PeerId.FromPublicKey(pub).Value != v.From) return false;
            return PeerIdentity.Verify(pub, Payload(roomKey, v), Convert.FromBase64String(v.Sig));
        }
        catch (FormatException) { return false; }
    }

    private static byte[] Payload(string roomKey, StoredVoice v)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-voice-v1"); w.Write(roomKey); w.Write(v.From); w.Write(v.Name); w.Write(v.Ts);
            w.Write(v.Active); w.Write(v.Muted); w.Write(v.Deafened);
        }
        return ms.ToArray();
    }
}
