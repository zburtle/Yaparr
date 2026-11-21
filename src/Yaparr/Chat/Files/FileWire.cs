using System.Buffers.Binary;

namespace Yaparr.Chat.Files;

/// <summary>
/// The messages on the swarm file channel (<see cref="ChannelIds.FileSwarm"/>). Every frame is a type byte and the 32-byte root of the file it is about, then what the type
/// carries. The channel is reliable and ordered, so a frame arrives whole and in order or the connection is gone.
/// </summary>
internal static class FileWire
{
    public const byte Interest = 1;      // I want this file: tell me what you have.
    public const byte Have = 2;          // everything I have of it, as a bitfield (also the answer to Interest)
    public const byte HaveMore = 3;      // one more piece I now have: u32 piece
    public const byte HashesWant = 4;    // u32 first, u16 count: the piece hashes from there
    public const byte HashesData = 5;    // u32 first, then 32 bytes for each hash
    public const byte Want = 6;          // u32 piece, u16 first block, u16 how many blocks
    public const byte Block = 7;         // u32 piece, u16 block, then the bytes
    public const byte Busy = 8;          // no free place now; then up to three ids (32 bytes each) of members that have pieces
    public const byte NotHave = 9;       // u32 piece: not that one (after all)
    public const byte Cancel = 10;       // u32 piece: stop sending it
    public const byte Gone = 11;         // I do not know that file (or you may not have it)

    public const int RootSize = 32;
    public const int Header = 1 + RootSize;
    public const int MaxHashesPerFrame = 4096;
    public const int MaxReferrals = 3;

    public static byte[] Frame(byte type, string root, int extra = 0)
    {
        var frame = new byte[Header + extra];
        frame[0] = type;
        Convert.FromHexString(root).CopyTo(frame, 1);
        return frame;
    }

    public static bool TryRoot(ReadOnlySpan<byte> frame, out string root)
    {
        root = "";
        if (frame.Length < Header) return false;
        root = Convert.ToHexStringLower(frame.Slice(1, RootSize));
        return true;
    }

    public static byte[] Simple(byte type, string root) => Frame(type, root);

    public static byte[] WithPiece(byte type, string root, int piece)
    {
        var frame = Frame(type, root, 4);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(Header), (uint)piece);
        return frame;
    }

    public static byte[] HaveBits(string root, PieceMap map)
    {
        var bits = map.ToBytes();
        var frame = Frame(Have, root, bits.Length);
        bits.CopyTo(frame, Header);
        return frame;
    }

    public static byte[] WantHashes(string root, int first, int count)
    {
        var frame = Frame(HashesWant, root, 6);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(Header), (uint)first);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(Header + 4), (ushort)count);
        return frame;
    }

    public static byte[] HashesBlock(string root, int first, ReadOnlySpan<byte> hashes)
    {
        var frame = Frame(HashesData, root, 4 + hashes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(Header), (uint)first);
        hashes.CopyTo(frame.AsSpan(Header + 4));
        return frame;
    }

    public static byte[] WantPiece(string root, int piece, int firstBlock, int blocks)
    {
        var frame = Frame(Want, root, 8);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(Header), (uint)piece);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(Header + 4), (ushort)firstBlock);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(Header + 6), (ushort)blocks);
        return frame;
    }

    public static byte[] BlockOf(string root, int piece, int block, ReadOnlySpan<byte> data)
    {
        var frame = Frame(Block, root, 6 + data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(Header), (uint)piece);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(Header + 4), (ushort)block);
        data.CopyTo(frame.AsSpan(Header + 6));
        return frame;
    }

    public static byte[] BusyWith(string root, IReadOnlyList<Identity.PeerId> referrals)
    {
        var chosen = referrals.Take(MaxReferrals).ToArray();
        var frame = Frame(Busy, root, 1 + chosen.Length * 32);
        frame[Header] = (byte)chosen.Length;
        for (int i = 0; i < chosen.Length; i++) Convert.FromHexString(chosen[i].Value).CopyTo(frame, Header + 1 + i * 32);
        return frame;
    }

    public static uint U32(ReadOnlySpan<byte> frame, int at) => BinaryPrimitives.ReadUInt32BigEndian(frame[at..]);

    public static int U16(ReadOnlySpan<byte> frame, int at) => BinaryPrimitives.ReadUInt16BigEndian(frame[at..]);
}
