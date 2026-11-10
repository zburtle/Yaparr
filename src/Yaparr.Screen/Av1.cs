namespace Yaparr.Screen;

/// <summary>
/// The few facts about an AV1 picture that Yaparr needs without decoding it (AV1 bitstream specification, sections 5.3 and 5.5). A frame of a share is one
/// temporal unit in the "low overhead" form that FFmpeg's IVF writer produces: a sequence of OBUs (open bitstream units), each with a one-byte header, usually
/// a size, and a payload. A key frame is the unit that carries the sequence header, which a viewer needs to start (the picture size and the profile are in it),
/// so the sender makes sure every key frame has one, as H.264 key frames repeat their parameter sets.
/// </summary>
public static class Av1
{
    public const int TemporalDelimiter = 2, SequenceHeader = 1, FrameHeader = 3, Frame = 6;

    /// <summary>One OBU in a temporal unit: its type and where its payload is.</summary>
    private readonly record struct Obu(int Type, int Start, int Length);

    // Walks the OBUs. Stops (and returns what it found) at anything that does not parse, so garbage never throws.
    private static IEnumerable<Obu> Walk(byte[] data)
    {
        int at = 0;
        while (at < data.Length)
        {
            byte header = data[at++];
            if ((header & 0x80) != 0) yield break;             // forbidden bit
            int type = (header >> 3) & 0xF;
            bool extension = (header & 0x04) != 0, hasSize = (header & 0x02) != 0;
            if (extension) at++;
            long size;
            if (hasSize)
            {
                // leb128: seven bits at a time, the top bit says there is more.
                size = 0;
                for (int i = 0; i < 8; i++)
                {
                    if (at >= data.Length) yield break;
                    byte b = data[at++];
                    size |= (long)(b & 0x7F) << (i * 7);
                    if ((b & 0x80) == 0) break;
                }
            }
            else size = data.Length - at;                      // the last OBU may leave its size out
            if (size < 0 || at + size > data.Length) yield break;
            yield return new Obu(type, at, (int)size);
            at += (int)size;
        }
    }

    /// <summary>A key frame is the temporal unit that holds a sequence header.</summary>
    public static bool IsKeyFrame(ReadOnlySpan<byte> frame)
    {
        var data = frame.ToArray();
        foreach (var obu in Walk(data)) if (obu.Type == SequenceHeader) return true;
        return false;
    }

    /// <summary>Whether this looks like a temporal unit: it starts with a temporal delimiter, which FFmpeg's IVF writer puts in front of every one.</summary>
    public static bool LooksLikeTemporalUnit(ReadOnlySpan<byte> frame) => frame.Length >= 2 && frame[0] == 0x12 && frame[1] == 0x00;

    /// <summary>The largest picture size of the stream, from its sequence header.</summary>
    public static bool TryGetSize(ReadOnlySpan<byte> frame, out int width, out int height)
    {
        width = height = 0;
        var data = frame.ToArray();
        foreach (var obu in Walk(data))
        {
            if (obu.Type != SequenceHeader) continue;
            return TryParseSequenceHeader(data.AsSpan(obu.Start, obu.Length), out width, out height);
        }
        return false;
    }

    private ref struct Bits
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _at;
        public bool Overrun;
        public Bits(ReadOnlySpan<byte> data) { _data = data; _at = 0; Overrun = false; }

        public uint Read(int count)
        {
            uint value = 0;
            for (int i = 0; i < count; i++)
            {
                int byteAt = _at >> 3;
                if (byteAt >= _data.Length) { Overrun = true; return 0; }
                value = (value << 1) | (uint)((_data[byteAt] >> (7 - (_at & 7))) & 1);
                _at++;
            }
            return value;
        }

        public bool Flag() => Read(1) != 0;

        // uvlc(): a run of zero bits, a one, then that many bits.
        public void SkipUvlc()
        {
            int zeros = 0;
            while (!Overrun && Read(1) == 0) { zeros++; if (zeros >= 32) return; }
            Read(zeros);
        }
    }

    private static bool TryParseSequenceHeader(ReadOnlySpan<byte> payload, out int width, out int height)
    {
        width = height = 0;
        var bits = new Bits(payload);
        bits.Read(3);                                  // seq_profile
        bits.Read(1);                                  // still_picture
        bool reduced = bits.Flag();                    // reduced_still_picture_header
        if (reduced) bits.Read(5);                     // seq_level_idx[0]
        else
        {
            bool timingInfo = bits.Flag();
            int bufferDelayLength = 0;
            bool decoderModelInfo = false;
            if (timingInfo)
            {
                bits.Read(32); bits.Read(32);          // num_units_in_display_tick, time_scale
                if (bits.Flag()) bits.SkipUvlc();      // equal_picture_interval, num_ticks_per_picture_minus_1
                decoderModelInfo = bits.Flag();
                if (decoderModelInfo)
                {
                    bufferDelayLength = (int)bits.Read(5) + 1;
                    bits.Read(32);                     // num_units_in_decoding_tick
                    bits.Read(5); bits.Read(5);        // buffer_removal_time_length_minus_1, frame_presentation_time_length_minus_1
                }
            }
            bool initialDisplayDelay = bits.Flag();
            int operatingPoints = (int)bits.Read(5) + 1;
            for (int i = 0; i < operatingPoints && !bits.Overrun; i++)
            {
                bits.Read(12);                         // operating_point_idc
                if (bits.Read(5) > 7) bits.Read(1);    // seq_level_idx, seq_tier
                if (decoderModelInfo && bits.Flag())   // decoder_model_present_for_this_op
                {
                    bits.Read(bufferDelayLength); bits.Read(bufferDelayLength); bits.Read(1);
                }
                if (initialDisplayDelay && bits.Flag()) bits.Read(4);
            }
        }
        int widthBits = (int)bits.Read(4) + 1, heightBits = (int)bits.Read(4) + 1;
        width = (int)bits.Read(widthBits) + 1;
        height = (int)bits.Read(heightBits) + 1;
        if (bits.Overrun || width < 2 || height < 2) { width = height = 0; return false; }
        return true;
    }

    /// <summary>
    /// A temporal unit made of a temporal delimiter, a sequence header for a picture of this size (the simplest one a decoder would take: main profile, one
    /// operating point, no timing information) and padding standing for the frame. Enough for <see cref="IsKeyFrame"/> and <see cref="TryGetSize"/>. For tests.
    /// </summary>
    public static byte[] FakeKeyFrame(int width, int height, int totalBytes = 64)
    {
        // seq_profile 0, still_picture 0, reduced_still_picture_header 0, timing_info_present 0, initial_display_delay_present 0,
        // operating_points_cnt_minus_1 0 (5 bits), operating_point_idc 0 (12), seq_level_idx 8 (5, above 7 so a tier bit follows), tier 0.
        var bits = new List<bool>();
        void Put(uint value, int count) { for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Put(0, 3); Put(0, 1); Put(0, 1); Put(0, 1); Put(0, 1); Put(0, 5); Put(0, 12); Put(8, 5); Put(0, 1);
        int widthBits = Math.Max(1, 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(width - 1)));
        int heightBits = Math.Max(1, 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)(height - 1)));
        Put((uint)(widthBits - 1), 4); Put((uint)(heightBits - 1), 4);
        Put((uint)(width - 1), widthBits); Put((uint)(height - 1), heightBits);
        while (bits.Count % 8 != 0) bits.Add(false);
        var header = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) header[i / 8] |= (byte)(0x80 >> (i % 8));

        var unit = new List<byte> { 0x12, 0x00 };                                    // temporal delimiter, size 0
        unit.Add((SequenceHeader << 3) | 0x02); unit.Add((byte)header.Length); unit.AddRange(header);
        int padding = Math.Max(0, totalBytes - unit.Count - 2);
        unit.Add((Frame << 3) | 0x02); unit.Add((byte)Math.Min(padding, 127)); unit.AddRange(new byte[Math.Min(padding, 127)]);
        return unit.ToArray();
    }
}
