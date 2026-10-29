using System.Buffers.Binary;
using System.Text;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Chat;

/// <summary>A signed statement by the root of a media group that it exists (or has ended).</summary>
public sealed record StoredMedia(string From, string Pub, string Group, string Kind, string Name, long Ts, bool Active, int Fanout, bool Signed, bool Upstream, string Sig);

/// <summary>
/// A stream shared with a room, such as a screen or a large voice channel. It reaches the viewers through a tree
/// rooted at <see cref="Root"/>: the root sends to a few members, who pass it on to a few more.
/// </summary>
/// <param name="Signed">The root signs every packet, so a member passing it on cannot change it. Only the root sends.</param>
/// <param name="Upstream">Anyone in the group can send, and packets travel both up and down the tree (voice).</param>
public sealed record MediaGroupInfo(string Id, string Kind, string Name, PeerId Root, bool Signed, bool Upstream);

/// <summary>One packet of a stream. <paramref name="IsKey"/> marks a packet a viewer can start from.</summary>
public sealed record MediaPacket(PeerId Origin, ReadOnlyMemory<byte> Payload, bool IsKey);

/// <summary>Where a node stands in a group's tree, and what it has done.</summary>
public sealed record MediaStatus(string Group, bool IsRoot, PeerId? Parent, IReadOnlyList<PeerId> Children, long Delivered, long Forwarded, long Dropped, long Duplicates)
{
    public bool Attached => IsRoot || Parent != null;
}

internal static class SignedMedia
{
    private const int MaxName = 64, MaxKind = 16;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    public static StoredMedia Create(PeerIdentity identity, string roomKey, string kind, string group, string name, long timestamp, bool active, int fanout, bool signed, bool upstream)
    {
        var unsigned = new StoredMedia(identity.Id.Value, Convert.ToBase64String(identity.PublicKey), group, kind,
            name.Length <= MaxName ? name : name[..MaxName], timestamp, active, fanout, signed, upstream, "");
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, unsigned))) };
    }

    public static bool Verify(StoredMedia m, string roomKey)
    {
        if (m.Name.Length > MaxName || m.Kind.Length is 0 or > MaxKind || m.Fanout is < 0 or > 64) return false;
        if (m.Group.Length != 16 || !m.Group.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        var now = DateTimeOffset.UtcNow;
        if (m.Ts > now.Add(MaxFutureSkew).ToUnixTimeMilliseconds() || m.Ts < now.Subtract(MaxAge).ToUnixTimeMilliseconds()) return false;
        try
        {
            var pub = Convert.FromBase64String(m.Pub);
            if (PeerId.FromPublicKey(pub).Value != m.From) return false;
            return PeerIdentity.Verify(pub, Payload(roomKey, m), Convert.FromBase64String(m.Sig));
        }
        catch (FormatException) { return false; }
    }

    private static byte[] Payload(string roomKey, StoredMedia m)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write("yaparr-media-v1"); w.Write(roomKey); w.Write(m.From); w.Write(m.Group); w.Write(m.Kind); w.Write(m.Name);
            w.Write(m.Ts); w.Write(m.Active); w.Write(m.Fanout); w.Write(m.Signed); w.Write(m.Upstream);
        }
        return ms.ToArray();
    }
}

/// <summary>The control messages between neighbours in a group's tree. They travel on the chat connection.</summary>
internal sealed class WireMedia
{
    /// <summary>join, place, attach, ok, refuse, leave, ping or link.</summary>
    public string Op { get; set; } = "";
    public string Group { get; set; } = "";
    /// <summary>For place: the member to attach to (empty if there is no room). For link: the member that took the child on.</summary>
    public string? Parent { get; set; }
    /// <summary>For link: the member that attached or detached.</summary>
    public string? Child { get; set; }
    /// <summary>How many children the sender could take.</summary>
    public int Cap { get; set; }
    public string[]? Exclude { get; set; }
    public bool Add { get; set; }
}

internal static class MediaWire
{
    public const int HeaderSize = 18, SignatureSize = 64, DefaultHops = 8;
    private const byte Version = 1, VersionMask = 0x1F, KeyFlag = 0x20;

    public static int MaxPayload(bool signed) => PeerConnection.MaxUnreliablePayload - HeaderSize - (signed ? SignatureSize : 0);

    public static ulong Origin8(PeerId id) => BinaryPrimitives.ReadUInt64BigEndian(Convert.FromHexString(id.Value.AsSpan(0, 16)));

    public static uint TagOf(string group) => BinaryPrimitives.ReadUInt32BigEndian(Convert.FromHexString(group.AsSpan(0, 8)));

    public static byte[] Pack(uint tag, ulong origin, uint seq, byte hops, bool key, ReadOnlySpan<byte> payload, PeerIdentity? signer)
    {
        var packet = new byte[HeaderSize + payload.Length + (signer != null ? SignatureSize : 0)];
        packet[0] = (byte)(Version | (key ? KeyFlag : 0));
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), tag);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(5), origin);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(13), seq);
        packet[17] = hops;
        payload.CopyTo(packet.AsSpan(HeaderSize));
        if (signer != null) signer.Sign(SignedBytes(packet.AsSpan(0, HeaderSize + payload.Length))).CopyTo(packet.AsSpan(HeaderSize + payload.Length));
        return packet;
    }

    public static bool TryParseHeader(ReadOnlySpan<byte> d, out uint tag, out ulong origin, out uint seq, out byte hops, out bool key)
    {
        tag = 0; origin = 0; seq = 0; hops = 0; key = false;
        if (d.Length <= HeaderSize || (d[0] & VersionMask) != Version) return false;
        key = (d[0] & KeyFlag) != 0;
        tag = BinaryPrimitives.ReadUInt32BigEndian(d[1..]);
        origin = BinaryPrimitives.ReadUInt64BigEndian(d[5..]);
        seq = BinaryPrimitives.ReadUInt32BigEndian(d[13..]);
        hops = d[17];
        return true;
    }

    // What is signed: the header without the hop count (forwarders change that) followed by the payload.
    public static byte[] SignedBytes(ReadOnlySpan<byte> unsignedPacket)
    {
        var bytes = new byte[unsignedPacket.Length - 1];
        unsignedPacket[..17].CopyTo(bytes);
        unsignedPacket[HeaderSize..].CopyTo(bytes.AsSpan(17));
        return bytes;
    }
}

/// <summary>Remembers which packets of one sender have arrived, so a packet that comes twice is used once.</summary>
internal sealed class SeqWindow
{
    private bool _any;
    private uint _max;
    private ulong _bits;

    public bool Accept(uint seq)
    {
        if (!_any) { _any = true; _max = seq; _bits = 1; return true; }
        int diff = (int)(seq - _max);
        if (diff > 0)
        {
            _bits = diff >= 64 ? 0 : _bits << diff;
            _bits |= 1;
            _max = seq;
            return true;
        }
        int back = -diff;
        if (back >= 64) return false;
        ulong mask = 1UL << back;
        if ((_bits & mask) != 0) return false;
        _bits |= mask;
        return true;
    }
}
