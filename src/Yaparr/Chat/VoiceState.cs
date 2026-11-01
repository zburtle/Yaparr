using System.Text;
using Yaparr.Identity;

namespace Yaparr.Chat;

/// <summary>A signed statement that someone is in (or has left) a room's voice channel.</summary>
/// <param name="Chan">The voice channel, or null for the first one.</param>
public sealed record StoredVoice(string From, string Pub, string Name, long Ts, bool Active, bool Muted, bool Deafened, string Sig, string? Chan = null);

internal sealed record VoiceEntry(StoredVoice State, long SeenAt);

/// <summary>Who is in a room's voice channel and whether they have muted themselves.</summary>
/// <param name="Channel">The voice channel they are in (<see cref="RoomChannels.DefaultVoice"/> for the first one).</param>
/// <summary>How many people are in the voice channel you are in, and which of them you cannot reach right now.</summary>
public sealed record VoiceReach(int Expected, IReadOnlyList<PeerId> Unreachable)
{
    /// <summary>How many of the people in the call you are reachable by.</summary>
    public int Reachable => Expected - Unreachable.Count;
}

public sealed record VoiceParticipant(PeerInfo Peer, bool Muted, bool Deafened, bool IsSelf, string Channel = RoomChannels.DefaultVoice);

internal static class SignedVoice
{
    private const int MaxName = 64;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    public static StoredVoice Create(PeerIdentity identity, string roomKey, string name, long timestamp, bool active, bool muted, bool deafened, string? channel = null)
    {
        var unsigned = new StoredVoice(
            identity.Id.Value, Convert.ToBase64String(identity.PublicKey),
            name.Length <= MaxName ? name : name[..MaxName], timestamp, active, muted, deafened, "", string.IsNullOrEmpty(channel) || channel == RoomChannels.DefaultVoice ? null : channel);
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, unsigned))) };
    }

    public static bool Verify(StoredVoice v, string roomKey)
    {
        if (v.Name.Length > MaxName) return false;
        if (v.Chan is { } chan && (chan.Length != 16 || !chan.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')) || chan == RoomChannels.DefaultVoice)) return false;
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
            // The first voice channel is signed exactly as before.
            w.Write(v.Chan == null ? "yaparr-voice-v1" : "yaparr-voice-v2"); w.Write(roomKey); w.Write(v.From); w.Write(v.Name); w.Write(v.Ts);
            w.Write(v.Active); w.Write(v.Muted); w.Write(v.Deafened);
            if (v.Chan != null) w.Write(v.Chan);
        }
        return ms.ToArray();
    }
}
