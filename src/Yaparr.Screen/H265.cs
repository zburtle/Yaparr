namespace Yaparr.Screen;

/// <summary>
/// The few facts about H.265 (HEVC) that Yaparr needs without decoding it, as <see cref="H264"/> does for H.264. A frame is one access unit of an Annex B byte
/// stream: NAL units, each behind a 00 00 01 start code and a two byte header, beginning with an access unit delimiter (the encoders are asked to write them).
/// Only the graphics cards encode it here (see <see cref="ScreenFeatures.H265"/> for the switch that turns the whole thing off).
/// </summary>
public static class H265
{
    // ITU-T H.265, table 7-1.
    private const int NalTrailR = 1, NalBlaWLp = 16, NalCra = 21, NalVps = 32, NalSps = 33, NalPps = 34, NalDelimiter = 35;

    /// <summary>The NAL units in the access unit, in order: type, and where the unit (from its two header bytes) starts and ends. Units of another layer are left out.</summary>
    internal static List<(int Type, int Start, int End)> Units(ReadOnlySpan<byte> data)
    {
        var list = new List<(int, int, int)>();
        int p = H264.FindStartCode(data, 0);
        while (p >= 0)
        {
            int start = p + 3;
            int next = H264.FindStartCode(data, start);
            int end = next < 0 ? data.Length : next;
            while (end > start && data[end - 1] == 0) end--;     // the zero of a four byte start code, and trailing zeros, are not part of the unit
            // forbidden_zero_bit 0, nuh_layer_id 0 (the video itself) and nuh_temporal_id_plus1 at least 1.
            if (end - start >= 2 && (data[start] & 0x81) == 0 && (data[start + 1] & 0xF8) == 0 && (data[start + 1] & 7) != 0)
                list.Add(((data[start] >> 1) & 0x3F, start, end));
            p = next;
        }
        return list;
    }

    /// <summary>
    /// A key frame can be decoded without any earlier frame: it holds a picture that starts a sequence (IDR, BLA or CRA) and the video, sequence and picture parameter sets that
    /// go with it (the encoders repeat them with every key frame; a frame without them would be no use to a viewer who has just joined).
    /// </summary>
    public static bool IsKeyFrame(ReadOnlySpan<byte> frame)
    {
        bool picture = false, vps = false, sps = false, pps = false;
        foreach (var (type, _, _) in Units(frame))
        {
            if (type is >= NalBlaWLp and <= NalCra) picture = true;
            else if (type == NalVps) vps = true;
            else if (type == NalSps) sps = true;
            else if (type == NalPps) pps = true;
        }
        return picture && vps && sps && pps;
    }

    /// <summary>Whether this is H.265 that holds the parameter sets and a starting picture, whatever this build does with it (so a viewer can say it cannot play it).</summary>
    public static bool LooksLikeKeyFrame(ReadOnlySpan<byte> frame) => IsKeyFrame(frame);

    /// <summary>The picture size, from the sequence parameter set of a key frame, with the cropping applied.</summary>
    public static bool TryGetSize(ReadOnlySpan<byte> frame, out int width, out int height)
    {
        width = height = 0;
        foreach (var (type, start, end) in Units(frame))
        {
            if (type != NalSps) continue;
            var rbsp = RemoveEmulationPrevention(frame[(start + 2)..end]);
            try { return ParseSize(rbsp, out width, out height); }
            catch (InvalidDataException) { return false; }
        }
        return false;
    }

    /// <summary>Whether this is a slice of a picture (as opposed to parameters, delimiters and the like), for tests.</summary>
    internal static bool HasPicture(ReadOnlySpan<byte> frame)
    {
        foreach (var (type, _, _) in Units(frame)) if (type < NalVps && (type <= 9 || type is >= NalBlaWLp and <= NalCra)) return true;
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

    // ITU-T H.265, 7.3.2.2.1 (sequence parameter set) and 7.3.3 (profile, tier and level): only as far as the picture size.
    private static bool ParseSize(byte[] sps, out int width, out int height)
    {
        width = height = 0;
        var r = new BitReader(sps);
        r.Skip(4);                                  // sps_video_parameter_set_id
        int maxSubLayersMinus1 = r.Bits(3);
        r.Skip(1);                                  // sps_temporal_id_nesting_flag
        ProfileTierLevel(r, maxSubLayersMinus1);
        r.UE();                                     // sps_seq_parameter_set_id
        int chromaFormat = r.UE();
        if (chromaFormat > 3) return false;
        bool separatePlanes = false;
        if (chromaFormat == 3) separatePlanes = r.Bits(1) == 1;
        long picWidth = r.UE(), picHeight = r.UE();
        int left = 0, right = 0, top = 0, bottom = 0;
        if (r.Bits(1) == 1) { left = r.UE(); right = r.UE(); top = r.UE(); bottom = r.UE(); }   // the conformance window: the cropping

        int chromaArray = separatePlanes ? 0 : chromaFormat;
        int unitX = chromaArray is 1 or 2 ? 2 : 1;
        int unitY = chromaArray == 1 ? 2 : 1;
        long w = picWidth - unitX * (long)(left + right);
        long h = picHeight - unitY * (long)(top + bottom);
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return false;
        width = (int)w;
        height = (int)h;
        return true;
    }

    // The general profile (88 bits) and level (8), then what each lower temporal layer says about itself.
    private static void ProfileTierLevel(BitReader r, int maxSubLayersMinus1)
    {
        r.Skip(88);   // profile space, tier, profile idc, the 32 compatibility flags, four more flags and 44 reserved or constraint bits
        r.Skip(8);    // general_level_idc
        var profilePresent = new bool[8];
        var levelPresent = new bool[8];
        for (int i = 0; i < maxSubLayersMinus1; i++) { profilePresent[i] = r.Bits(1) == 1; levelPresent[i] = r.Bits(1) == 1; }
        if (maxSubLayersMinus1 > 0) for (int i = maxSubLayersMinus1; i < 8; i++) r.Skip(2);
        for (int i = 0; i < maxSubLayersMinus1; i++)
        {
            if (profilePresent[i]) r.Skip(88);
            if (levelPresent[i]) r.Skip(8);
        }
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

        public void Skip(int count)
        {
            if (_pos + count > _data.Length * 8) throw new InvalidDataException("The parameter set ends too early.");
            _pos += count;
        }

        // Exp-Golomb: zeros, a one, then as many bits as there were zeros.
        public int UE()
        {
            int zeros = 0;
            while (Bits(1) == 0) { if (++zeros > 30) throw new InvalidDataException(); }
            return zeros == 0 ? 0 : (int)((1u << zeros) - 1 + (uint)Bits(zeros));
        }
    }

    /// <summary>
    /// The start of a key frame for a picture of this size: a delimiter, video, sequence and picture parameter sets (the sequence one is real, in Main profile, 4:2:0 with
    /// the cropping that gives the size) and an IDR slice of the given length. It is not decodable. For tests.
    /// </summary>
    public static byte[] FakeKeyFrame(int width, int height, int totalBytes = 96, int maxSubLayersMinus1 = 0)
    {
        // The size is in units of the smallest coding block (8 pixels), and the conformance window crops to what was asked, in units of two pixels.
        int codedWidth = (width + 7) / 8 * 8, codedHeight = (height + 7) / 8 * 8;
        int cropRight = (codedWidth - width) / 2, cropBottom = (codedHeight - height) / 2;
        var w = new BitWriter();
        w.Bits(0, 4);                  // video parameter set id
        w.Bits(maxSubLayersMinus1, 3); // max sub layers - 1
        w.Bits(1, 1);                  // temporal id nesting
        w.Bits(0, 2); w.Bits(0, 1); w.Bits(1, 5);   // profile space 0, tier 0 (Main), profile idc 1 (Main)
        w.Bits(0, 32);                 // compatibility flags
        w.Bits(0, 4);                  // progressive, interlaced, non packed, frame only
        w.Bits(0, 44);                 // reserved
        w.Bits(93, 8);                 // level 3.1
        // What each lower temporal layer says about itself: the first has its own profile, all have a level (a parser has to skip exactly this much).
        for (int i = 0; i < maxSubLayersMinus1; i++) { w.Bits(i == 0 ? 1 : 0, 1); w.Bits(1, 1); }
        if (maxSubLayersMinus1 > 0) for (int i = maxSubLayersMinus1; i < 8; i++) w.Bits(0, 2);
        for (int i = 0; i < maxSubLayersMinus1; i++) { if (i == 0) w.Bits(0, 88); w.Bits(90, 8); }
        w.UE(0);                       // sps id
        w.UE(1);                       // chroma format 4:2:0
        w.UE(codedWidth); w.UE(codedHeight);
        if (cropRight > 0 || cropBottom > 0) { w.Bits(1, 1); w.UE(0); w.UE(cropRight); w.UE(0); w.UE(cropBottom); }
        else w.Bits(0, 1);
        w.Bits(1, 1);                  // stop bit (the rest of the parameter set is not read)
        var sps = w.ToBytes();

        var frame = new List<byte>();
        frame.AddRange(new byte[] { 0, 0, 0, 1, 0x46, 0x01, 0x50 });                       // access unit delimiter
        frame.AddRange(new byte[] { 0, 0, 0, 1, 0x40, 0x01, 0x0C, 0x01, 0xFF, 0xFF });       // video parameter set (not read)
        frame.AddRange(new byte[] { 0, 0, 0, 1, 0x42, 0x01 });
        frame.AddRange(Escape(sps));
        frame.AddRange(new byte[] { 0, 0, 0, 1, 0x44, 0x01, 0xC1, 0x72, 0xB4, 0x22, 0x40 }); // picture parameter set (not read)
        frame.AddRange(new byte[] { 0, 0, 0, 1, 0x26, 0x01 });                              // IDR_W_RADL slice
        while (frame.Count < totalBytes) frame.Add(0x55); // slice data: never zero, so it holds no start code
        return frame.ToArray();
    }

    /// <summary>A frame that is not a key frame: a delimiter and a TRAIL_R slice. For tests.</summary>
    public static byte[] FakeDeltaFrame(int totalBytes = 32)
    {
        var frame = new List<byte> { 0, 0, 0, 1, 0x46, 0x01, 0x30, 0, 0, 0, 1, (byte)(NalTrailR << 1), 0x01 };
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

        public void Bits(long value, int count)
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
            long v = (long)value + 1;
            int length = 0;
            for (long t = v; t > 0; t >>= 1) length++;
            Bits(0, length - 1);
            Bits(v, length);
        }

        public byte[] ToBytes() => _bytes.ToArray();
    }
}
