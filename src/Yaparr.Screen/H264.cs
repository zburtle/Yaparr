namespace Yaparr.Screen;

/// <summary>
/// The few facts about H.264 that Yaparr needs without decoding it. A frame is one access unit of an Annex B byte stream:
/// NAL units, each behind a 00 00 01 start code, beginning with an access unit delimiter (FFmpeg is asked to write them).
/// </summary>
public static class H264
{
    private const int NalSlice = 1, NalIdr = 5, NalSps = 7, NalPps = 8, NalDelimiter = 9;

    /// <summary>
    /// A delimiter to write after each access unit that is handed to a decoder. FFmpeg's parser only knows that a picture is
    /// complete when the next one starts; this makes it start at once, so a picture is not held back until the next arrives.
    /// </summary>
    public static ReadOnlySpan<byte> EndMarker => new byte[] { 0, 0, 0, 1, 9, 0xF0 };

    /// <summary>The position of the 00 00 01 at or after <paramref name="from"/>, or -1.</summary>
    internal static int FindStartCode(ReadOnlySpan<byte> data, int from)
    {
        for (int i = Math.Max(0, from); i + 2 < data.Length; i++)
        {
            if (data[i + 2] > 1) { i += 2; continue; } // no start code can begin at i, i+1 or i+2
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1) return i;
        }
        return -1;
    }

    /// <summary>The NAL unit types in the access unit, in order.</summary>
    private static List<(int Type, int Start, int End)> Units(ReadOnlySpan<byte> data)
    {
        var list = new List<(int, int, int)>();
        int p = FindStartCode(data, 0);
        while (p >= 0)
        {
            int start = p + 3;
            int next = FindStartCode(data, start);
            int end = next < 0 ? data.Length : next;
            // The zero before a four-byte start code, and trailing zeros, are not part of the unit.
            while (end > start && data[end - 1] == 0) end--;
            if (end > start) list.Add((data[start] & 0x1F, start, end));
            p = next;
        }
        return list;
    }

    /// <summary>
    /// A key frame can be decoded without any earlier frame: it holds an IDR picture and the parameter sets that go with it.
    /// (FFmpeg repeats them with every IDR picture; a frame without them would be no use to a viewer who has just joined.)
    /// </summary>
    public static bool IsKeyFrame(ReadOnlySpan<byte> frame)
    {
        bool idr = false, sps = false, pps = false;
        foreach (var (type, _, _) in Units(frame))
        {
            if (type == NalIdr) idr = true;
            else if (type == NalSps) sps = true;
            else if (type == NalPps) pps = true;
        }
        return idr && sps && pps;
    }

    /// <summary>The picture size, from the sequence parameter set of a key frame, with the cropping applied.</summary>
    public static bool TryGetSize(ReadOnlySpan<byte> frame, out int width, out int height)
    {
        width = height = 0;
        foreach (var (type, start, end) in Units(frame))
        {
            if (type != NalSps) continue;
            var rbsp = RemoveEmulationPrevention(frame[(start + 1)..end]);
            try { return ParseSize(rbsp, out width, out height); }
            catch (InvalidDataException) { return false; }
        }
        return false;
    }

    /// <summary>Whether this is a slice of a picture (as opposed to parameters, delimiters and the like), for tests.</summary>
    internal static bool HasPicture(ReadOnlySpan<byte> frame)
    {
        foreach (var (type, _, _) in Units(frame)) if (type is NalSlice or NalIdr) return true;
        return false;
    }

    // Inside a NAL unit 00 00 is followed by 03 whenever the next byte is 0..3; the 03 is not data.
    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> data)
    {
        var result = new List<byte>(data.Length);
        int zeros = 0;
        foreach (byte b in data)
        {
            if (zeros >= 2 && b == 3) { zeros = 0; continue; }
            result.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return result.ToArray();
    }

    // ITU-T H.264, 7.3.2.1.1: only as far as the picture size.
    private static bool ParseSize(byte[] sps, out int width, out int height)
    {
        width = height = 0;
        var r = new BitReader(sps);
        int profile = r.Bits(8);
        r.Bits(8); // constraint flags
        r.Bits(8); // level
        r.UE();    // parameter set id
        int chromaFormat = 1;
        bool separatePlanes = false;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chromaFormat = r.UE();
            if (chromaFormat == 3) separatePlanes = r.Bits(1) == 1;
            r.UE(); r.UE(); // bit depths
            r.Bits(1);      // lossless bypass
            if (r.Bits(1) == 1) // scaling matrices
            {
                for (int i = 0; i < (chromaFormat != 3 ? 8 : 12); i++)
                {
                    if (r.Bits(1) == 0) continue;
                    int last = 8, next = 8;
                    for (int j = 0; j < (i < 6 ? 16 : 64); j++)
                    {
                        if (next != 0) next = (last + r.SE() + 256) % 256;
                        last = next == 0 ? last : next;
                    }
                }
            }
        }
        r.UE(); // log2_max_frame_num
        int pocType = r.UE();
        if (pocType == 0) r.UE();
        else if (pocType == 1)
        {
            r.Bits(1); r.SE(); r.SE();
            int cycle = r.UE();
            if (cycle > 255) throw new InvalidDataException();
            for (int i = 0; i < cycle; i++) r.SE();
        }
        r.UE();      // reference frames
        r.Bits(1);   // gaps in frame numbers
        int mbWide = r.UE() + 1;
        int mbHigh = r.UE() + 1;
        int frameMbsOnly = r.Bits(1);
        if (frameMbsOnly == 0) r.Bits(1);
        r.Bits(1);   // direct 8x8
        int left = 0, right = 0, top = 0, bottom = 0;
        if (r.Bits(1) == 1) { left = r.UE(); right = r.UE(); top = r.UE(); bottom = r.UE(); }

        int chromaArray = separatePlanes ? 0 : chromaFormat;
        int unitX = chromaArray == 0 ? 1 : chromaArray == 3 ? 1 : 2;
        int unitY = (chromaArray == 0 ? 1 : chromaArray == 1 ? 2 : 1) * (2 - frameMbsOnly);
        width = mbWide * 16 - unitX * (left + right);
        height = (2 - frameMbsOnly) * mbHigh * 16 - unitY * (top + bottom);
        return width > 0 && height > 0 && width <= 16384 && height <= 16384;
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _pos; // in bits

        public BitReader(byte[] data) => _data = data;

        public int Bits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                if (_pos >= _data.Length * 8) throw new InvalidDataException("The parameter set ends too early.");
                value = (value << 1) | ((_data[_pos >> 3] >> (7 - (_pos & 7))) & 1);
                _pos++;
            }
            return value;
        }

        // Exp-Golomb: zeros, a one, then as many bits as there were zeros.
        public int UE()
        {
            int zeros = 0;
            while (Bits(1) == 0) { if (++zeros > 30) throw new InvalidDataException(); }
            return zeros == 0 ? 0 : (int)((1u << zeros) - 1 + (uint)Bits(zeros));
        }

        public int SE()
        {
            int k = UE();
            return (k & 1) == 1 ? (k + 1) / 2 : -(k / 2);
        }
    }

    /// <summary>
    /// The start of a key frame for a picture of this size: a delimiter, a parameter set that says so, a picture parameter set and
    /// an IDR slice of the given length. It is not decodable. For tests.
    /// </summary>
    public static byte[] FakeKeyFrame(int width, int height, int totalBytes = 64)
    {
        // Baseline profile, 4:2:0, frames only: the size is in macroblocks, and the cropping gives the rest.
        int mbWide = (width + 15) / 16, mbHigh = (height + 15) / 16;
        int cropRight = (mbWide * 16 - width) / 2, cropBottom = (mbHigh * 16 - height) / 2;
        var w = new BitWriter();
        w.Bits(66, 8); w.Bits(0, 8); w.Bits(30, 8);
        w.UE(0);                       // parameter set id
        w.UE(0);                       // log2_max_frame_num - 4
        w.UE(2);                       // picture order count type
        w.UE(1);                       // reference frames
        w.Bits(0, 1);                  // gaps
        w.UE(mbWide - 1); w.UE(mbHigh - 1);
        w.Bits(1, 1);                  // frames only
        w.Bits(0, 1);                  // direct 8x8
        if (cropRight > 0 || cropBottom > 0) { w.Bits(1, 1); w.UE(0); w.UE(cropRight); w.UE(0); w.UE(cropBottom); }
        else w.Bits(0, 1);
        w.Bits(0, 1);                  // no VUI
        w.Bits(1, 1);                  // stop bit
        var sps = w.ToBytes();

        var frame = new List<byte>();
        frame.AddRange(new byte[] { 0, 0, 0, 1, 9, 0xF0 });
        frame.AddRange(new byte[] { 0, 0, 0, 1, 0x67 });
        frame.AddRange(Escape(sps));
        frame.AddRange(new byte[] { 0, 0, 0, 1, 0x68, 0xCE, 0x3C, 0x80 });
        frame.AddRange(new byte[] { 0, 0, 0, 1, 0x65 });
        while (frame.Count < totalBytes) frame.Add(0x55); // slice data: never zero, so it holds no start code
        return frame.ToArray();
    }

    /// <summary>A frame that is not a key frame: a delimiter and a slice. For tests.</summary>
    public static byte[] FakeDeltaFrame(int totalBytes = 32)
    {
        var frame = new List<byte> { 0, 0, 0, 1, 9, 0x30, 0, 0, 0, 1, 0x41 };
        while (frame.Count < totalBytes) frame.Add(0x55);
        return frame.ToArray();
    }

    private static byte[] Escape(byte[] rbsp)
    {
        var result = new List<byte>(rbsp.Length + 2);
        int zeros = 0;
        foreach (byte b in rbsp)
        {
            if (zeros >= 2 && b <= 3) { result.Add(3); zeros = 0; }
            result.Add(b);
            zeros = b == 0 ? zeros + 1 : 0;
        }
        return result.ToArray();
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _bits;

        public void Bits(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                if (_bits % 8 == 0) _bytes.Add(0);
                if (((value >> i) & 1) == 1) _bytes[^1] |= (byte)(0x80 >> (_bits % 8));
                _bits++;
            }
        }

        public void UE(int value)
        {
            int v = value + 1, length = 0;
            for (int t = v; t > 0; t >>= 1) length++;
            Bits(0, length - 1);
            Bits(v, length);
        }

        public byte[] ToBytes() => _bytes.ToArray();
    }
}

/// <summary>
/// Cuts a stream of H.264 (as FFmpeg writes it on a pipe, with an access unit delimiter in front of every picture) into
/// access units. A unit is complete when the next delimiter arrives, so each is released one picture late.
/// </summary>
public sealed class H264Reader
{
    private readonly Stream _stream;
    private byte[] _buffer = new byte[64 * 1024];
    private int _length;

    public H264Reader(Stream stream) => _stream = stream;

    /// <summary>The next access unit, or null at the end of the stream.</summary>
    public async Task<byte[]?> ReadAsync(CancellationToken ct = default)
    {
        while (true)
        {
            int first = FindDelimiter(0);
            if (first >= 0 && FindDelimiter(first + 4) is var second and >= 0)
            {
                var unit = _buffer.AsSpan(first, second - first).ToArray();
                Array.Copy(_buffer, second, _buffer, 0, _length - second);
                _length -= second;
                return unit;
            }
            if (_length >= Ivf.MaxFrameBytes) throw new InvalidDataException("A frame that big is not plausible.");
            if (_length == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
            int n = await _stream.ReadAsync(_buffer.AsMemory(_length), ct).ConfigureAwait(false);
            if (n == 0)
            {
                // The end of the stream ends the last unit too; whatever came before the first delimiter is not a unit.
                if (first < 0 || _length - first <= 6) return null;
                var last = _buffer.AsSpan(first, _length - first).ToArray();
                _length = 0;
                return last;
            }
            _length += n;
        }
    }

    // Where the access unit delimiter at or after 'from' begins (including a zero in front of the start code), or -1.
    private int FindDelimiter(int from)
    {
        var data = _buffer.AsSpan(0, _length);
        int p = H264.FindStartCode(data, from);
        while (p >= 0)
        {
            if (p + 3 >= _length) return -1; // the NAL header has not arrived yet
            if ((data[p + 3] & 0x1F) == 9) return p > 0 && data[p - 1] == 0 && p - 1 >= from ? p - 1 : p;
            p = H264.FindStartCode(data, p + 3);
        }
        return -1;
    }
}
