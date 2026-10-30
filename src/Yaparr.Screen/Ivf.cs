using System.Buffers.Binary;
using System.Text;

namespace Yaparr.Screen;

/// <summary>
/// IVF is the plainest container for VP8 and VP9: a 32-byte header, then each frame behind a 12-byte header. FFmpeg writes
/// and reads it on a pipe, and it gives us the frame boundaries without parsing the codec.
/// </summary>
public sealed record IvfHeader(string FourCc, int Width, int Height, int TimebaseDenominator = 1000, int TimebaseNumerator = 1)
{
    public const int Size = 32;

    public static IvfHeader Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Size || !bytes[..4].SequenceEqual("DKIF"u8)) throw new InvalidDataException("This is not an IVF stream.");
        return new IvfHeader(
            Encoding.ASCII.GetString(bytes.Slice(8, 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]));
    }

    public byte[] ToBytes()
    {
        var b = new byte[Size];
        "DKIF"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), Size);
        Encoding.ASCII.GetBytes(FourCc.PadRight(4)[..4]).CopyTo(b, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), (ushort)Width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), (ushort)Height);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), (uint)TimebaseDenominator);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), (uint)TimebaseNumerator);
        return b;
    }
}

public static class Ivf
{
    /// <summary>The most a single frame may hold; a bigger one means the stream is damaged.</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    public static async Task<IvfHeader> ReadHeaderAsync(Stream stream, CancellationToken ct = default)
    {
        var b = new byte[IvfHeader.Size];
        await stream.ReadExactlyAsync(b, ct).ConfigureAwait(false);
        return IvfHeader.Parse(b);
    }

    /// <summary>The next frame, or null at the end of the stream.</summary>
    public static async Task<(byte[] Data, long Pts)?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var head = new byte[12];
        int got = 0;
        while (got < head.Length)
        {
            int n = await stream.ReadAsync(head.AsMemory(got), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (got == 0) return null;
                throw new EndOfStreamException("The stream ended in the middle of a frame header.");
            }
            got += n;
        }
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (size > MaxFrameBytes) throw new InvalidDataException($"A frame of {size} bytes is not plausible.");
        var data = new byte[size];
        await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        return (data, BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(4)));
    }

    public static byte[] FrameHeader(int size, long pts)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)size);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(4), pts);
        return b;
    }
}
