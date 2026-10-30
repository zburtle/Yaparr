using System.Buffers.Binary;
using System.Text;
using Yaparr.Identity;
using Yaparr.Transport;

namespace Yaparr.Chat;

/// <summary>A signed statement by the root of a media group that it exists (or has ended).</summary>
public sealed record StoredMedia(string From, string Pub, string Group, string Kind, string Name, long Ts, bool Active, int Fanout, bool Signed, bool Upstream, int Kbps, string Sig);

/// <summary>
/// A stream shared with a room, such as a screen or a large voice channel. It reaches the viewers through a tree
/// rooted at <see cref="Root"/>: the root sends to a few members, who pass it on to a few more.
/// </summary>
/// <param name="Signed">The root signs every packet, so a member passing it on cannot change it. Only the root sends.</param>
/// <param name="Upstream">Anyone in the group can send, and packets travel both up and down the tree (voice).</param>
/// <param name="Kbps">The bitrate the sharer says the stream needs, in kilobits per second. Zero if it did not say.</param>
public sealed record MediaGroupInfo(string Id, string Kind, string Name, PeerId Root, bool Signed, bool Upstream, int Kbps = 0);

/// <summary>One packet of a stream. <paramref name="IsKey"/> marks a packet a viewer can start from.</summary>
public sealed record MediaPacket(PeerId Origin, ReadOnlyMemory<byte> Payload, bool IsKey, uint Seq = 0);

/// <summary>
/// One whole frame of a stream sent with <see cref="MediaSource.SendFrameAsync"/>, put back together. A viewer gets
/// frames in order and without gaps: it starts at a key frame, and after a loss it waits for the next key frame
/// before it hands over anything else.
/// </summary>
public sealed record MediaFrame(PeerId Origin, uint Id, ReadOnlyMemory<byte> Data, bool IsKey);

/// <summary>Where a node stands in a group's tree, and what it has done.</summary>
/// <param name="OverServer">
/// The stream is too big for the shared relay server and the only way this node can reach the tree runs through it,
/// so it was not attached.
/// </param>
public sealed record MediaStatus(string Group, bool IsRoot, PeerId? Parent, IReadOnlyList<PeerId> Children, long Delivered, long Forwarded, long Dropped, long Duplicates,
    long Retransmitted = 0, long Recovered = 0, bool OverServer = false)
{
    public bool Attached => IsRoot || Parent != null;
}

internal static class SignedMedia
{
    private const int MaxName = 64, MaxKind = 16;
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    public static StoredMedia Create(PeerIdentity identity, string roomKey, string kind, string group, string name, long timestamp, bool active, int fanout, bool signed, bool upstream, int kbps)
    {
        var unsigned = new StoredMedia(identity.Id.Value, Convert.ToBase64String(identity.PublicKey), group, kind,
            name.Length <= MaxName ? name : name[..MaxName], timestamp, active, fanout, signed, upstream, kbps, "");
        return unsigned with { Sig = Convert.ToBase64String(identity.Sign(Payload(roomKey, unsigned))) };
    }

    public static bool Verify(StoredMedia m, string roomKey)
    {
        if (m.Name.Length > MaxName || m.Kind.Length is 0 or > MaxKind || m.Fanout is < 0 or > 64 || m.Kbps is < 0 or > 100_000) return false;
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
            w.Write(m.Ts); w.Write(m.Active); w.Write(m.Fanout); w.Write(m.Signed); w.Write(m.Upstream); w.Write(m.Kbps);
        }
        return ms.ToArray();
    }
}

/// <summary>The control messages between neighbours in a group's tree. They travel on the chat connection. Ops: join, place, attach, ok, refuse, leave, ping, link, key, nack.</summary>
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
    /// <summary>For nack: the packets the sender is missing.</summary>
    public uint[]? Seqs { get; set; }
}

internal static class MediaWire
{
    public const int HeaderSize = 18, SignatureSize = 64, DefaultHops = 8;
    private const byte Version = 1, VersionMask = 0x1F, KeyFlag = 0x20;

    /// <summary>Set on a packet given again from a cache: the receiver may have seen its number before and should use it anyway.</summary>
    public const byte ReplayFlag = 0x40;

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

    public static bool TryParseHeader(ReadOnlySpan<byte> d, out uint tag, out ulong origin, out uint seq, out byte hops, out bool key, out bool replay)
    {
        tag = 0; origin = 0; seq = 0; hops = 0; key = false; replay = false;
        if (d.Length <= HeaderSize || (d[0] & VersionMask) != Version) return false;
        key = (d[0] & KeyFlag) != 0;
        replay = (d[0] & ReplayFlag) != 0;
        tag = BinaryPrimitives.ReadUInt32BigEndian(d[1..]);
        origin = BinaryPrimitives.ReadUInt64BigEndian(d[5..]);
        seq = BinaryPrimitives.ReadUInt32BigEndian(d[13..]);
        hops = d[17];
        return true;
    }

    // What is signed: the header without the hop count and the replay flag (those change on the way) followed by the payload.
    public static byte[] SignedBytes(ReadOnlySpan<byte> unsignedPacket)
    {
        var bytes = new byte[unsignedPacket.Length - 1];
        unsignedPacket[..17].CopyTo(bytes);
        bytes[0] &= unchecked((byte)~ReplayFlag);
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

/// <summary>
/// Cuts a frame into packets and puts it back together. A fragment starts with nine bytes: flags (1 for a key frame),
/// the frame number, the fragment's place and how many there are.
/// </summary>
internal static class Fragments
{
    public const int HeaderSize = 9, MaxFrameBytes = 4 * 1024 * 1024;

    public static int Count(int frameLength, int maxPayload) => (frameLength + (maxPayload - HeaderSize) - 1) / (maxPayload - HeaderSize);

    public static byte[] Pack(uint frame, int index, int count, bool key, ReadOnlySpan<byte> piece)
    {
        var bytes = new byte[HeaderSize + piece.Length];
        bytes[0] = (byte)(key ? 1 : 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), frame);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(5), (ushort)index);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(7), (ushort)count);
        piece.CopyTo(bytes.AsSpan(HeaderSize));
        return bytes;
    }

    public static bool TryParse(ReadOnlySpan<byte> payload, out uint frame, out int index, out int count, out bool key, out ReadOnlySpan<byte> piece)
    {
        frame = 0; index = count = 0; key = false; piece = default;
        if (payload.Length <= HeaderSize) return false;
        key = (payload[0] & 1) != 0;
        frame = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(payload[1..]);
        index = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(payload[5..]);
        count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(payload[7..]);
        piece = payload[HeaderSize..];
        return count > 0 && index < count && count <= MaxFrameBytes / 256;
    }
}

/// <summary>
/// Turns a stream of packets back into frames. Frames come out in order. A frame that is still incomplete when its
/// time is up (a short wait for stragglers) counts as lost; then everything until the next key frame is thrown away,
/// because it would not decode, and the caller is told to ask for a key frame.
/// </summary>
internal sealed class FrameAssembler
{
    private sealed class Partial
    {
        public byte[]?[] Pieces = Array.Empty<byte[]?>();
        public int Have;
        public bool Key;
        public long FirstSeenAt;
        public PeerId Origin;
        public bool Complete => Have == Pieces.Length;
    }

    private readonly SortedDictionary<uint, Partial> _frames = new(Comparer<uint>.Create((a, b) => (int)(a - b)));
    private readonly long _waitMs, _createdAt;
    private bool _waitingForKey = true, _lost;
    private uint _expected;
    private long _lastRequestAt = -1, _expectedSince;

    public FrameAssembler(long reorderWaitMs, long now) { _waitMs = reorderWaitMs; _createdAt = now; }

    public long Delivered { get; private set; }
    public long Lost { get; private set; }
    public bool WaitingForKey => _waitingForKey;

    /// <summary>Takes one packet. Finished frames are added to <paramref name="ready"/>; true is returned when the sender should be asked for a key frame.</summary>
    public bool Add(PeerId origin, ReadOnlySpan<byte> payload, long now, List<MediaFrame> ready)
    {
        if (Fragments.TryParse(payload, out var id, out var index, out var count, out var key, out var piece)
            && (_waitingForKey || (int)(id - _expected) >= 0))
        {
            if (!_frames.TryGetValue(id, out var p))
            {
                if (_frames.Count >= 64) DropOldest();
                _frames[id] = p = new Partial { Pieces = new byte[]?[count], Key = key, FirstSeenAt = now, Origin = origin };
            }
            if (p.Pieces.Length == count && p.Pieces[index] == null)
            {
                p.Pieces[index] = piece.ToArray();
                p.Have++;
            }
        }
        return Drain(now, ready);
    }

    /// <summary>Call now and then, so a frame that never completes is noticed.</summary>
    public bool Tick(long now, List<MediaFrame> ready) => Drain(now, ready);

    private void DropOldest()
    {
        _frames.Remove(_frames.Keys.First());
        Lost++;
        _waitingForKey = true;
        _lost = true;
    }

    private bool Drain(long now, List<MediaFrame> ready)
    {
        while (true)
        {
            if (_waitingForKey)
            {
                // The newest complete key frame is the place to start; anything older is no use now.
                uint? start = null;
                foreach (var (id, p) in _frames)
                    if (p.Key && p.Complete) start = id;
                if (start is { } s)
                {
                    foreach (var older in _frames.Keys.Where(k => (int)(k - s) < 0).ToList()) _frames.Remove(older);
                    _expected = s;
                    _expectedSince = now;
                    _waitingForKey = false;
                    continue;
                }
                foreach (var stale in _frames.Where(kv => now - kv.Value.FirstSeenAt > 4 * _waitMs).Select(kv => kv.Key).ToList()) _frames.Remove(stale);
                break;
            }

            if (_frames.TryGetValue(_expected, out var next) && next.Complete)
            {
                _frames.Remove(_expected);
                ready.Add(new MediaFrame(next.Origin, _expected, Join(next), next.Key));
                Delivered++;
                _expected++;
                _expectedSince = now;
                continue;
            }

            // The frame we need is not complete. Once we have waited for it, and what is waiting behind it has too, it is not coming.
            if (_frames.Count > 0 && now - Math.Max(_frames.First().Value.FirstSeenAt, _expectedSince) > _waitMs)
            {
                var firstId = _frames.First().Key;
                Lost += Math.Max(1, (int)(firstId - _expected));
                _frames.Remove(_expected);
                _waitingForKey = true;
                _lost = true;
                continue;
            }
            return false;
        }

        // Waiting for a key frame. Just after joining, the one the parent keeps may still be on its way.
        if (_lastRequestAt < 0 && !_lost && now - _createdAt < 1500) return false;
        if (_lastRequestAt >= 0 && now - _lastRequestAt < (_lost ? 1000 : 1500)) return false;
        _lastRequestAt = now;
        _lost = false;
        return true;
    }

    private static byte[] Join(Partial p)
    {
        var all = new byte[p.Pieces.Sum(x => x!.Length)];
        int at = 0;
        foreach (var piece in p.Pieces) { piece!.CopyTo(all, at); at += piece.Length; }
        return all;
    }
}
